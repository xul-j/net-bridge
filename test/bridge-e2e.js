// End-to-end test of the .NET bridge hosting bridge/demo/LegacyOrders.exe (a plain WinForms app).
// Needs the bridge running (see README) and the xul-j base repo: XULJ_BASE (default ../xul-j).
// Run: node test/bridge-e2e.js [http://127.0.0.1:8092]
'use strict';
const assert = require('assert');
const path = require('path');
const BASE = path.resolve(process.env.XULJ_BASE || path.join(__dirname, '../../xul-j'));
const { validate } = require(`${BASE}/protocol/validate`);
const { Model, renderText } = require(`${BASE}/protocol/model`);
const { stream, intent } = require(`${BASE}/clients/sse`);
const http = require('http');

function request(method, url, body, headers = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request(url, { method, headers }, (res) => {
      let data = '';
      res.on('data', (c) => { data += c; });
      res.on('end', () => resolve({ status: res.statusCode, body: data, headers: res.headers }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

const base = process.argv[2] || 'http://127.0.0.1:8092';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(pred, what, ms = 8000) {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (pred()) return; await sleep(25); }
  throw new Error(`timed out waiting for: ${what}`);
}
let passed = 0;
async function step(name, fn) { await fn(); passed++; console.log(`  ok  ${name}`); }

function client(session) {
  const model = new Model();
  const ops = [];
  const conn = stream(base, session, { onOp: (op) => { ops.push(op); model.apply(op); } });
  const attr = (id, k) => { const n = model.ids.get(id); return n && model.resolved(n)[k]; };
  const rows = (src) => model.sources.get(src) || [];
  const send = async (msg, expect = 202) => {
    const r = await intent(base, session, msg);
    assert.strictEqual(r.status, expect, `${JSON.stringify(msg)} → ${r.status} ${r.body}`);
  };
  return { model, ops, conn, attr, rows, send };
}

(async () => {
  const session = `bridge${Date.now().toString(36)}`;
  const a = client(session);
  a.dialog = () => a.model.root.children.find((w) => w.attrs.modal && !w.attrs.hidden);
  a.dialogButton = (label) => [...a.model.ids.values()].find((n) => n.tag === 'button' && n.attrs.label === label && isIn(n, a.dialog()));
  const dialog = a.dialog;
  const dialogButton = a.dialogButton;

  await step('the WinForms form arrives as XUL-J; every op is schema-valid and seq is gapless', async () => {
    await until(() => a.model.ids.has('ordersGrid') && a.model.ids.has('clockLabel'), 'form rendered');
    a.ops.forEach((op, i) => {
      assert.deepStrictEqual(validate(op), [], JSON.stringify(op));
      assert.strictEqual(op.seq, i + 1);
    });
    assert.strictEqual(a.attr('MainForm', 'label'), 'Legacy Orders');
  });

  await step('dock order and inferred rows follow the designer layout', async () => {
    assert.deepStrictEqual(a.model.ids.get('MainForm').children.filter((c) => c.tag !== 'menupopup').map((c) => c.id), ['menu', 'tools', 'tabs', 'status']);
    const row0 = a.model.ids.get('customerLabel').parent;
    assert.deepStrictEqual(row0.children.map((c) => c.id), ['customerLabel', 'customerText']);
    assert.strictEqual(a.attr('customerText', 'flex'), 1, 'Anchor Left|Right → flex');
    assert.strictEqual(a.attr('customerLabel', 'width'), a.attr('productLabel', 'width'), 'label column aligned');
    const row2 = a.model.ids.get('addButton').parent.children.map((c) => c.tag);
    assert.deepStrictEqual(row2, ['checkbox', 'spacer', 'button'], 'Anchor Right → spacer before');
    assert.strictEqual(a.model.commands.get('cmd_addButton').key, 'alt+a');
    assert.strictEqual(a.model.commands.get('cmd_newOrderItem').key, 'ctrl+n');
    assert.strictEqual(a.attr('apiKeyText', 'password'), true);
  });

  await step('MenuStrip becomes a real menubar: menus, separators, access keys, shortcuts', async () => {
    const bar = a.model.ids.get('menu');
    assert.strictEqual(bar.tag, 'menubar');
    assert.deepStrictEqual(bar.children.map((c) => `${c.id}:${c.tag}:${c.attrs.label}:${c.attrs.accesskey}`),
      ['file:menu:File:alt+f', 'edit:menu:Edit:alt+e', 'help:menu:Help:alt+h']);
    assert.deepStrictEqual(bar.children[0].children.map((c) => c.tag), ['menuitem', 'menuitem', 'menuitem', 'menuitem', 'menuseparator', 'menuitem']);
    assert.strictEqual(a.attr('exitItem', 'label'), 'Exit', 'no more "File › Exit" flattening');
    assert.strictEqual(a.model.commands.get('cmd_importItem').key, 'ctrl+o');
  });

  await step('theme tokens come from the app; a Firebrick label becomes a danger label', async () => {
    assert(a.model.theme, 'theme op sent');
    assert.deepStrictEqual(validate({ op: 'theme', tokens: a.model.theme.tokens }), []);
    assert.match(a.model.theme.tokens.accent, /^#[0-9a-f]{6}$/);
    assert(['compact', 'normal', 'comfortable'].includes(a.model.theme.tokens.density));
    assert.strictEqual(a.attr('errorLabel', 'class'), 'danger');
    assert.strictEqual(a.attr('ordersGrid', 'seltype'), 'multiple');
    assert.strictEqual(a.attr('ordersGrid', 'contextmenu'), 'gridMenu');
    assert.strictEqual(a.model.ids.get('gridMenu').tag, 'menupopup');
  });

  await step("the app's own Timer drives live updates", async () => {
    const t0 = a.attr('clockLabel', 'value');
    await until(() => a.attr('clockLabel', 'value') !== t0, 'clock ticks', 3000);
  });

  await step('validation in the app shows its error label', async () => {
    await a.send({ op: 'do', command: 'cmd_addButton' });
    await until(() => a.attr('errorLabel', 'value') === 'Customer is required.', 'error label');
  });

  await step('typing into real controls and clicking Add puts a row in the DataGridView', async () => {
    await a.send({ op: 'input', id: 'customerText', value: 'ACME' });
    await a.send({ op: 'input', id: 'productCombo', value: '2' });
    await a.send({ op: 'input', id: 'quantityUpDown', value: '3' });
    await a.send({ op: 'input', id: 'expressCheck', value: true });
    await a.send({ op: 'do', command: 'cmd_addButton' });
    await until(() => a.rows('ordersGrid').length === 1, 'grid row');
    assert.deepStrictEqual(Object.values(a.rows('ordersGrid')[0]), ['1001', 'ACME', 'Gizmo', '3', 'yes', '26.75 EUR']);
    await until(() => a.attr('statusLabel', 'value') === 'Added order for ACME', 'status');
    assert.strictEqual(a.attr('customerText', 'value'), '', 'app cleared the field');
    assert.strictEqual(a.attr('errorLabel', 'value'), '');
  });

  await step('ToolStripComboBox and RadioButton changes run the app handlers', async () => {
    await a.send({ op: 'input', id: 'currencyBox', value: '1' });
    await until(() => a.rows('ordersGrid')[0].c5 === '26.75 USD', 'currency');
    await a.send({ op: 'input', id: 'roundUp', value: true });
    await until(() => a.rows('ordersGrid')[0].c5 === '27.00 USD', 'rounding');
    assert.strictEqual(a.attr('roundNone', 'value'), false, 'radio group exclusivity is WinForms’ own');
  });

  await step('timer-driven import: button disabled while running, 409 for a disabled command', async () => {
    await a.send({ op: 'do', command: 'cmd_importButton' });
    await until(() => a.model.commands.get('cmd_importButton').disabled === true, 'disabled');
    await a.send({ op: 'do', command: 'cmd_importButton' }, 409);
    await until(() => a.attr('importProgress', 'value') > 0.3, 'progress');
    await until(() => a.rows('importLog').length === 25 && a.model.commands.get('cmd_importButton').disabled === false, 'import done', 15000);
    assert.strictEqual(a.rows('ordersGrid').length, 26);
    assert.strictEqual(a.attr('statusLabel', 'value'), 'Import finished: 25 orders');
  });

  await step('grid selection both ways, double-click, and a context menu driven by the app’s Opening handler', async () => {
    assert(a.rows('ordersGrid').length >= 3, 'rows to work with');
    await a.send({ op: 'select', id: 'ordersGrid', rows: [0, 2] });
    await until(() => JSON.stringify(a.attr('ordersGrid', 'selection')) === '[0,2]' && a.attr('statusLabel', 'value') === '2 selected', 'selection applied in the app');
    await a.send({ op: 'select', id: 'ordersGrid', rows: [999] }, 400);
    await a.send({ op: 'contextmenu', id: 'gridMenu', target: 'ordersGrid' });
    await until(() => a.attr('deleteSelectedItem', 'label') === 'Delete 2 orders…', 'Opening handler ran');
    assert.strictEqual(a.model.commands.get('cmd_duplicateItem').disabled, true, 'duplicate needs exactly one');
    await a.send({ op: 'contextmenu', id: 'gridMenu', target: 'customerText' }, 404);
    await a.send({ op: 'select', id: 'ordersGrid', rows: [1] });
    await until(() => JSON.stringify(a.attr('ordersGrid', 'selection')) === '[1]', 'single');
    await a.send({ op: 'contextmenu', id: 'gridMenu', target: 'ordersGrid' });
    await until(() => a.model.commands.get('cmd_duplicateItem').disabled === false, 'duplicate enabled');
    const before = a.rows('ordersGrid').length;
    const customer = a.rows('ordersGrid')[1].c1;
    await a.send({ op: 'do', command: 'cmd_duplicateItem' });
    await until(() => a.rows('ordersGrid').length === before + 1 && a.rows('ordersGrid')[before].c1 === customer, 'duplicated');
    await a.send({ op: 'activate', id: 'ordersGrid', row: 0 });
    await until(() => a.dialog() && a.dialog().attrs.label === 'Order details', 'double-click → details');
    assert.match(a.dialog().children[0].attrs.value, /^Order \d+ for /);
    await a.send({ op: 'do', command: a.dialogButton('OK').attrs.command });
    await until(() => !a.dialog(), 'closed');
  });

  await step('a second browser session gets its own form instance', async () => {
    const b = client(`${session}b`);
    await until(() => b.model.ids.has('ordersGrid') && b.model.ids.has('clockLabel'), 'second form');
    assert.strictEqual(b.rows('ordersGrid').length, 0);
    assert(a.rows('ordersGrid').length > 20, 'the first session keeps its orders');
    b.conn.close();
  });

  await step('a reconnecting client resumes; a fresh one rebuilds the same UI', async () => {
    a.conn.close();
    const last = a.model.lastSeq;
    const had = a.rows('ordersGrid').length;
    await a.send({ op: 'do', command: 'cmd_deleteLastButton' }); // happens while disconnected
    const resumed = [];
    a.conn = stream(base, session, { from: last, onOp: (op) => { resumed.push(op); a.model.apply(op); } });
    await until(() => a.rows('ordersGrid').length === had - 1, 'missed delete replayed');
    assert(resumed.every((op) => op.seq > last));
    const fresh = new Model();
    const c = stream(base, session, { onOp: (op) => fresh.apply(op) });
    await until(() => fresh.lastSeq >= a.model.lastSeq - 1 && renderText(fresh).split('\n').slice(0, -3).join() === renderText(a.model).split('\n').slice(0, -3).join(), 'fresh client converges', 5000);
    c.close();
  });


  const show = (w) => console.log(renderText({ ...a.model, root: { tag: 'root', children: [w], attrs: {} }, resolved: a.model.resolved.bind(a.model) })
    .split('\n').map((l) => `      ${l}`).join('\n'));

  await step('MessageBox.Show becomes a modal window with icon, message and default button', async () => {
    assert.strictEqual(a.attr('confirmCheck', 'value'), true);
    const count = a.rows('ordersGrid').length;
    await a.send({ op: 'do', command: 'cmd_clearButton' });
    await until(() => dialog(), 'dialog window');
    show(dialog());
    assert.strictEqual(dialog().attrs.icon, 'warning');
    assert.strictEqual(dialog().children[0].attrs.value, `Delete all ${count} orders?`);
    assert.strictEqual(dialogButton('No').attrs.class, 'primary', 'MessageBoxDefaultButton.Button2');
    assert.strictEqual(a.model.commands.get('cmd_clearButton').disabled, true, 'owner is disabled while modal');
    await a.send({ op: 'do', command: dialogButton('Yes').attrs.command });
    await until(() => !dialog() && a.rows('ordersGrid').length === 0, 'grid cleared after Yes');
  });

  await step('Interaction.InputBox becomes a prompt; the answer flows back into the app', async () => {
    await a.send({ op: 'do', command: 'cmd_defaultCustomerItem' });
    await until(() => dialog() && a.model.ids.has('response'), 'input box');
    assert.strictEqual(dialog().attrs.icon, 'question');
    await a.send({ op: 'input', id: 'response', value: 'Globex' });
    await a.send({ op: 'do', command: dialogButton('OK').attrs.command });
    await until(() => !dialog() && a.attr('customerText', 'value') === 'Globex', 'default customer applied');
  });

  await step('SaveFileDialog: the app writes a temp file and the browser is told to download it', async () => {
    await a.send({ op: 'do', command: 'cmd_addButton' });
    await until(() => a.rows('ordersGrid').length === 1, 'one order');
    await a.send({ op: 'do', command: 'cmd_exportItem' });
    await until(() => dialog() && a.model.ids.has('fileName'), 'save dialog');
    assert.strictEqual(a.attr('fileName', 'value'), 'orders.csv');
    await a.send({ op: 'input', id: 'fileName', value: 'march.csv' });
    await a.send({ op: 'do', command: dialogButton('Save').attrs.command });
    await until(() => a.model.transient.some((t) => t.op === 'download'), 'download op', 5000);
    const dl = a.model.transient.find((t) => t.op === 'download');
    assert.deepStrictEqual(validate(dl), []);
    assert.strictEqual(dl.name, 'march.csv');
    const file = await request('GET', base + dl.url);
    assert.strictEqual(file.status, 200);
    assert.match(file.headers['content-disposition'], /attachment; filename="march.csv"/);
    assert.strictEqual(file.body.trim().split('\n')[1].split(',').slice(1, 5).join(','), 'Globex,Gizmo,3,yes'); // product, qty and express kept from the earlier order
    assert.strictEqual((await request('GET', `${base}/download/${'0'.repeat(32)}`)).status, 404);
  });

  await step('OpenFileDialog: the browser uploads a file and the app reads it from a temp path', async () => {
    await a.send({ op: 'do', command: 'cmd_importItem' });
    await until(() => dialog() && a.model.ids.has('picker'), 'open dialog');
    assert.strictEqual(a.attr('picker', 'accept'), '.csv');
    assert.strictEqual(a.model.commands.get(dialogButton('Open').attrs.command).disabled, true, 'Open waits for a file');
    const csv = 'order,customer,product,qty\n1,Initech,Gadget,2\n2,Hooli,Gizmo,1\nbroken line\n';
    const up = await request('POST', `${base}/upload?session=${session}&id=picker`, csv, { 'X-Filename': encodeURIComponent('legacy export.csv') });
    assert.strictEqual(up.status, 202, up.body);
    await until(() => a.attr('picker', 'value') === 'legacy export.csv', 'picker shows file');
    await until(() => a.model.commands.get(dialogButton('Open').attrs.command).disabled === false, 'Open enabled');
    await a.send({ op: 'do', command: dialogButton('Open').attrs.command });
    await until(() => a.rows('ordersGrid').length === 3, 'two rows imported');
    await until(() => dialog() && dialog().attrs.icon === 'warning', 'skipped-lines warning');
    assert.match(dialog().children[0].attrs.value, /^1 lines could not be read/);
    await a.send({ op: 'do', command: dialogButton('OK').attrs.command });
    await until(() => !dialog() && a.attr('statusLabel', 'value') === 'Imported 2 orders from legacy export.csv', 'import status');
    const bad = await request('POST', `${base}/upload?session=${session}&id=customerText`, 'x', { 'X-Filename': 'x.txt' });
    assert.strictEqual(bad.status, 404, 'uploads only reach file pickers');
  });

  await step('dialogs without a browser equivalent are cancelled with a notification', async () => {
    await a.send({ op: 'do', command: 'cmd_printItem' });
    await until(() => a.model.transient.some((t) => t.op === 'notify' && /PrintDialog/.test(t.message)), 'notify');
    await until(() => a.attr('statusLabel', 'value') === 'Print cancelled', 'app saw Cancel');
  });

  await step('File › Exit closes the form; the session ends cleanly', async () => {
    await a.send({ op: 'do', command: 'cmd_exitItem' });
    await until(() => a.model.ids.has('ended') && !a.model.ids.has('MainForm'), 'ended');
  });

  console.log(`\n${passed} checks passed.`);
  a.conn.close();
  process.exit(0);
})().catch((e) => { console.error('FAIL', e); process.exit(1); });

function isIn(n, ancestor) {
  for (let p = n; p; p = p.parent) if (p === ancestor) return true;
  return false;
}
