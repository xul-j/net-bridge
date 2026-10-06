# XUL-J WinForms bridge

`xulj-host.exe` serves an existing, unmodified WinForms application to browsers. Each browser
session gets its own instance of the app's main form, kept off-screen. The bridge renders the
live control tree to XUL-J ops over SSE, using the same protocol and browser client as
[xul-j/xul-j](https://github.com/xul-j/xul-j), and replays browser intents as clicks and edits on the real controls.

    mono xulj-host.exe App.exe [--form Namespace.MainForm] [--port 8092] [--host *]

Or, inside an app you can change, replace `Application.Run(new MainForm())` with
`XulJBridge.Run(() => new MainForm())`.

## Build and run on Linux (Mono + Xvfb in Docker)

The build embeds the browser client from the base repo, so clone both side by side:

    git clone https://github.com/xul-j/xul-j && git clone https://github.com/xul-j/net-bridge
    cd net-bridge
    docker build -t xulj-mono docker
    ./build.sh                      # → out/xulj-host.exe, out/LegacyOrders.exe (XULJ_BASE=../xul-j)
    docker run -d --init --name xulj-bridge -p 8092:8092 -v "$PWD/out":/app:ro \
      xulj-mono xvfb-run -a mono /app/xulj-host.exe /app/LegacyOrders.exe --port 8092
    node test/bridge-e2e.js         # end-to-end checks against the running bridge

`--init` matters: as PID 1, `xvfb-run` never sees Xvfb's ready signal and hangs silently.

On Windows, build the same sources with the .NET Framework `csc` (see `build.sh` for the
flags). HttpListener needs a URL ACL for `--host *`
(`netsh http add urlacl url=http://*:8092/ user=Everyone`), or use `--host localhost`.

## How it works

- `Renderer.cs` walks the forms and builds a virtual XUL-J tree. Layout is inferred:
  - `Dock` becomes nested vbox/hbox, processed in WinForms' reverse z-order;
  - absolutely placed controls are grouped into rows by vertical overlap;
  - `Anchor Left|Right` becomes `flex`, and `Anchor Right` alone puts a spacer before the control;
  - each control's width runs up to the next control's `Left`, which reproduces the designer's column alignment.
- `Reconciler.cs` diffs each frame (every 50 ms) against the previous one and emits the minimal
  ops: removals, then updates, then inserts, then row data (append when possible).
- `Bridge.cs` hosts the UI thread, sessions and HttpListener. An intent is validated
  synchronously, which yields 404/409, then run with `BeginInvoke`, so a modal dialog can't block HTTP.
  Forms the app opens (dialogs, extra windows) go to the session that acted last.
- Clicks call the control's `OnClick` (or `ToolStripItem.PerformClick`), so app handlers and
  `Button.DialogResult` behave as usual. `&` mnemonics become `alt+` keys, and menu `ShortcutKeys` carry over.

## Mapping

| WinForms | XUL-J |
|---|---|
| Form | window (extra forms and dialogs become more windows) |
| Button, ToolStripButton, ToolStripMenuItem | button / toolbarbutton + command |
| MenuStrip | toolbar; leaf items are flattened to "File › Exit" |
| TextBox, MaskedTextBox, RichTextBox, NumericUpDown | textbox (`password` for PasswordChar) |
| CheckBox, RadioButton | checkbox (radio exclusivity stays WinForms' own) |
| ComboBox, ToolStripComboBox | menulist |
| DataGridView, ListView, ListBox | tree with a row source |
| ProgressBar, TrackBar, ToolStripProgressBar | progressmeter |
| TabControl / TabPage | tabbox / tabpanel |
| GroupBox, Panel, SplitContainer, FlowLayoutPanel, TableLayoutPanel, UserControl | groupbox / boxes |
| anything else | a muted `[TypeName]` placeholder |

## Limitations

- The app's `Main()` is not run (xulj-host instantiates the form), so setup done in `Main` is skipped.
- On Windows, `MessageBox.Show` is a native dialog the bridge cannot see. A session that opens
  one waits until someone answers it on the host. Mono's MessageBox is a Form, so it works there.
- Owner-drawn controls, custom painting and images do not render. Menus are flattened. Grid and
  list selection, and cell editing, are not mapped yet.
- Every session is a live form instance on the host, with no authentication and no session limit.
- Layout inference covers designer-style forms; heavily overlapping absolute layouts come out as rows.
