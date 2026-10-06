// An agent's-eye test: the XUL-J MCP server (from the base repo) operating LegacyOrders.exe
// through net-bridge. Needs the bridge running and ../xul-j (XULJ_BASE).
// Run: node test/mcp-e2e.js [http://127.0.0.1:8092]
'use strict';
const assert = require('assert');
const path = require('path');
const readline = require('readline');
const { spawn } = require('child_process');
const BASE = path.resolve(process.env.XULJ_BASE || path.join(__dirname, '../../xul-j'));
const url = process.argv[2] || 'http://127.0.0.1:8092';

const child = spawn(process.execPath, [path.join(BASE, 'mcp/server.js'), '--url', url], { stdio: ['pipe', 'pipe', 'ignore'] });
const pending = new Map();
let next = 1;
readline.createInterface({ input: child.stdout }).on('line', (l) => { const m = JSON.parse(l); pending.get(m.id)?.(m); pending.delete(m.id); });
const rpc = (method, params) => new Promise((r) => { const id = next++; pending.set(id, r); child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`); });
const call = async (name, args = {}) => {
  const r = (await rpc('tools/call', { name, arguments: args })).result;
  if (r.isError) throw new Error(`${name} failed: ${r.content[0].text}`);
  return r.content[0].text;
};
const find = (text, re, what) => { const m = re.exec(text); assert(m, `${what} not found in:\n${text}`); return m; };

let passed = 0;
async function step(name, fn) { await fn(); passed++; console.log(`  ok  ${name}`); }

(async () => {
  await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'agent-test', version: '1' } });
  let ui;

  await step('the agent reads the legacy app as an outline', async () => {
    ui = await call('wait_for', { text: 'Legacy Orders' });
    find(ui, /window "Legacy Orders" \[MainForm\]/, 'window');
    find(ui, /item "Export CSV…" \[exportItem\] → cmd_exportItem \(ctrl\+s\)/, 'menu item with shortcut');
    find(ui, /table \[ordersGrid\] 0 rows; columns: Order #, Customer, Product, Qty, Express, Total; multiple selection; right-click menu \[gridMenu\]/, 'grid');
  });

  await step('it learns from the validation error and fixes the form', async () => {
    let r = await call('do_command', { command: 'cmd_addButton' });
    find(r, /[+~] label "Customer is required\." \[errorLabel\] \(danger\)/, 'validation message, with its role');
    await call('set_value', { id: 'customerText', value: 'Initech' });
    await call('set_value', { id: 'productCombo', value: 'Gadget' });
    await call('set_value', { id: 'quantityUpDown', value: 4 });
    r = await call('do_command', { command: 'cmd_addButton' });
    find(r, /~ table \[ordersGrid\] 1 rows/, 'row added');
    const t = JSON.parse(await call('read_table', { id: 'ordersGrid' }));
    assert.deepStrictEqual([t.rows[0].Customer, t.rows[0].Product, t.rows[0].Qty, t.rows[0].Total], ['Initech', 'Gadget', '4', '48.00 EUR']);
  });

  await step('right-click menu: the app decides what is enabled', async () => {
    await call('select_rows', { id: 'ordersGrid', rows: [0] });
    const menu = await call('open_context_menu', { id: 'ordersGrid' });
    find(menu, /item "Duplicate order" \[duplicateItem\] → cmd_duplicateItem$/m, 'duplicate enabled');
    find(menu, /item "Delete order…" \[deleteSelectedItem\]/, 'label updated by Opening');
    const r = await call('do_command', { command: 'cmd_duplicateItem' });
    find(r, /~ table \[ordersGrid\] 2 rows/, 'duplicated');
  });

  await step('a confirmation dialog is answered through its buttons', async () => {
    await call('select_rows', { id: 'ordersGrid', rows: [1] });
    await call('open_context_menu', { id: 'ordersGrid' });
    let r = await call('do_command', { command: 'cmd_deleteSelectedItem' });
    find(r, /A modal dialog is open: "Confirm"/, 'dialog');
    ui = await call('get_ui');
    const yes = find(ui, /button "Yes" \[[\w.-]+\] → (cmd_[\w.-]+)/, 'Yes button')[1];
    r = await call('do_command', { command: yes });
    find(r, /~ table \[ordersGrid\] 1 rows/, 'deleted');
  });

  await step('save dialog → the agent downloads the CSV the app wrote', async () => {
    await call('do_command', { command: 'cmd_exportItem' });
    ui = await call('get_ui');
    await call('set_value', { id: 'fileName', value: 'agent-export.csv' });
    const save = find(ui, /button "Save" \[[\w.-]+\] → (cmd_[\w.-]+)/, 'Save button')[1];
    const r = await call('do_command', { command: save });
    find(r, /~ label "Exported 1 orders to agent-export\.csv"/, 'app wrote the file');
    find(r, /~ enabled again: file, edit, help/, 'the dialog closing is one summary line');
    const csv = await call('download_file', { name: 'agent-export.csv' });
    find(csv, /^order,customer,product,qty,express,total$/m, 'CSV header');
    find(csv, /,Initech,Gadget,4,,48\.00 EUR/, 'CSV row');
  });

  await step('open dialog → the agent uploads a file the app imports', async () => {
    await call('do_command', { command: 'cmd_importItem' });
    await call('upload_file', { id: 'picker', name: 'more.csv', content: 'order,customer,product,qty\n1,Hooli,Widget,2\n2,Globex,Gizmo,5\n' });
    ui = await call('get_ui');
    const open = find(ui, /button "Open" \[[\w.-]+\] → (cmd_[\w.-]+)/, 'Open button')[1];
    const r = await call('do_command', { command: open });
    find(r, /~ table \[ordersGrid\] 3 rows/, 'imported');
  });

  console.log(`\n${passed} checks passed.`);
  child.kill();
  process.exit(0);
})().catch((e) => { console.error('FAIL', e.message); child.kill(); process.exit(1); });
