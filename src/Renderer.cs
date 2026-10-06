// Renders a live WinForms control tree into XUL-J elements (a virtual tree, diffed by Reconciler).
// Layout is inferred: Dock becomes nested boxes, absolutely placed controls become rows,
// Anchor Left|Right becomes flex. Unknown controls degrade to a labelled placeholder.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace XulJ.Bridge
{
    public sealed class VNode
    {
        public string Id;
        public string Tag;
        public string Parent;
        public int Order;
        public Dictionary<string, object> Attrs = new Dictionary<string, object>();
        public List<Dictionary<string, object>> Rows; // tree data, when Tag == "tree"
    }

    sealed class Box
    {
        public bool Horizontal;
        public object Flex;
        public readonly List<object> Items = new List<object>(); // Control, ToolStripItem or Box
        public readonly Dictionary<object, Dictionary<string, object>> Extra = new Dictionary<object, Dictionary<string, object>>();
    }

    public sealed class Renderer
    {
        const int HGap = 6; // matches the gap of .x-hbox in xul.css

        readonly Dictionary<object, string> ids = new Dictionary<object, string>();
        readonly Dictionary<string, object> byId = new Dictionary<string, object>();
        readonly HashSet<string> used = new HashSet<string>();
        readonly Dictionary<object, object> hostedBy = new Dictionary<object, object>();
        List<VNode> nodes;
        Dictionary<string, Dictionary<string, object>> commands;

        public List<VNode> Nodes => nodes;
        public Dictionary<string, Dictionary<string, object>> Commands => commands;

        public object Lookup(string id) { object o; return byId.TryGetValue(id, out o) ? o : null; }

        public void Render(IList<Form> forms)
        {
            nodes = new List<VNode>();
            commands = new Dictionary<string, Dictionary<string, object>>();
            var live = new HashSet<object>();
            for (int i = 0; i < forms.Count; i++)
            {
                var f = forms[i];
                if (f.IsDisposed) continue;
                var win = Add(f, "window", "root", i, live);
                win.Attrs["label"] = f.Text ?? "";
                if (!f.Visible) win.Attrs["hidden"] = true;
                if (f.Modal) win.Attrs["modal"] = true;
                if (f is BridgeDialog bd)
                {
                    if (bd.IconKind != null) win.Attrs["icon"] = bd.IconKind;
                    AddBox(win.Id + "__msg", "description", win.Id, -1).Attrs["value"] = bd.Message;
                }
                else
                {
                    // Mono paints MessageBox text instead of using a Label; surface it as one.
                    var msg = f.GetType().GetField("msgbox_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (msg != null) AddBox(win.Id + "__msg", "description", win.Id, -1).Attrs["value"] = msg.GetValue(f) as string ?? "";
                }
                LayoutChildren(f, win.Id, live);
            }
            // Forget controls that are gone so their ids can be reused.
            foreach (var dead in ids.Keys.Where(k => !live.Contains(k)).ToList())
            {
                byId.Remove(ids[dead]);
                used.Remove(ids[dead]);
                ids.Remove(dead);
            }
        }

        // ---- ids ---------------------------------------------------------------

        // nameFrom lets a hosted control (ToolStripComboBox.ComboBox) use its strip item's name.
        string IdOf(object o, object nameFrom = null)
        {
            string id;
            if (ids.TryGetValue(o, out id)) return id;
            var named = nameFrom ?? o;
            string name = (named as Control)?.Name ?? (named as ToolStripItem)?.Name ?? "";
            name = Regex.Replace(name, "[^A-Za-z0-9_.-]", "");
            if (name.Length == 0 || !Regex.IsMatch(name, "^[A-Za-z_]")) name = o.GetType().Name.ToLowerInvariant();
            id = name;
            for (int n = 2; used.Contains(id) || id.Contains("__"); n++) id = name.Replace("__", "_") + n;
            used.Add(id);
            ids[o] = id;
            byId[id] = o;
            return id;
        }

        VNode Add(object source, string tag, string parent, int order, HashSet<object> live)
        {
            live.Add(source);
            object nameFrom;
            hostedBy.TryGetValue(source, out nameFrom);
            var n = new VNode { Id = IdOf(source, nameFrom), Tag = tag, Parent = parent, Order = order };
            nodes.Add(n);
            return n;
        }

        VNode AddBox(string id, string tag, string parent, int order)
        {
            var n = new VNode { Id = id, Tag = tag, Parent = parent, Order = order };
            nodes.Add(n);
            return n;
        }

        // ---- layout --------------------------------------------------------------

        void LayoutChildren(Control container, string parentId, HashSet<object> live)
        {
            var kids = container.Controls.Cast<Control>().ToList();
            if (kids.Count == 0) return;

            if (container is FlowLayoutPanel flow)
            {
                int i = 0;
                foreach (var c in kids) Element(c, parentId, i++, null, live);
                return;
            }
            if (container is TableLayoutPanel table)
            {
                var rows = kids.GroupBy(c => table.GetPositionFromControl(c).Row).OrderBy(g => g.Key).ToList();
                int r = 0;
                foreach (var row in rows)
                {
                    var hb = AddBox(parentId + "__t" + r, "hbox", parentId, r);
                    int col = 0;
                    foreach (var c in row.OrderBy(c => table.GetPositionFromControl(c).Column))
                        Element(c, hb.Id, col++, StretchesHorizontally(c) ? (object)1 : null, live);
                    r++;
                }
                return;
            }

            // WinForms docks the last control in the collection first.
            var docked = kids.Where(c => c.Dock != DockStyle.None).Reverse().ToList();
            var free = kids.Where(c => c.Dock == DockStyle.None).ToList();
            var tree = DockBox(docked, 0, free);
            if (tree == null) return;
            // The container itself is a vbox; splice a vertical root into it directly.
            if (!tree.Horizontal) EmitItems(tree, parentId, live);
            else EmitBox(tree, parentId, 0, parentId + "__d", live);
        }

        Box DockBox(List<Control> docked, int idx, List<Control> free)
        {
            if (idx == docked.Count) return free.Count > 0 ? FreeBox(free) : null;
            var c = docked[idx];
            var rest = DockBox(docked, idx + 1, free);
            Box b;
            switch (c.Dock)
            {
                case DockStyle.Top:
                case DockStyle.Bottom:
                    b = new Box { Horizontal = false };
                    if (c.Dock == DockStyle.Bottom) Append(b, rest);
                    b.Items.Add(c);
                    b.Extra[c] = SizeHint(c, vertical: true);
                    if (c.Dock == DockStyle.Top) Append(b, rest);
                    return b;
                case DockStyle.Left:
                case DockStyle.Right:
                    b = new Box { Horizontal = true };
                    if (c.Dock == DockStyle.Right) Append(b, rest);
                    b.Items.Add(c);
                    b.Extra[c] = new Dictionary<string, object> { { "width", Math.Max(0, c.Width) } };
                    if (c.Dock == DockStyle.Left) Append(b, rest);
                    return b;
                default: // Fill
                    b = new Box { Horizontal = false };
                    b.Items.Add(c);
                    b.Extra[c] = new Dictionary<string, object> { { "flex", 1 } };
                    if (rest != null) b.Items.Add(rest);
                    return b;
            }
        }

        // The rest of a dock sequence fills the remaining space; same-direction boxes merge.
        static void Append(Box b, Box rest)
        {
            if (rest == null) return;
            rest.Flex = 1;
            if (rest.Horizontal == b.Horizontal)
            {
                b.Items.AddRange(rest.Items);
                foreach (var kv in rest.Extra) b.Extra[kv.Key] = kv.Value;
            }
            else b.Items.Add(rest);
        }

        // Absolutely positioned controls: group into rows by vertical overlap, order rows by Top.
        Box FreeBox(List<Control> free)
        {
            var col = new Box { Horizontal = false, Flex = 1 };
            var sorted = free.OrderBy(c => c.Top).ThenBy(c => c.Left).ToList();
            var rows = new List<List<Control>>();
            int bottom = int.MinValue;
            foreach (var c in sorted)
            {
                if (rows.Count == 0 || c.Top >= bottom - 4) { rows.Add(new List<Control>()); bottom = int.MinValue; }
                rows[rows.Count - 1].Add(c);
                bottom = Math.Max(bottom, c.Top + Math.Min(c.Height, 40));
            }
            foreach (var row in rows)
            {
                var hb = new Box { Horizontal = true };
                var cells = row.OrderBy(c => c.Left).ToList();
                bool stretchV = false;
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    var extra = new Dictionary<string, object>();
                    bool stretchH = StretchesHorizontally(c);
                    if ((c.Anchor & AnchorStyles.Right) != 0 && (c.Anchor & AnchorStyles.Left) == 0 && !hb.Items.OfType<string>().Any())
                    {
                        hb.Items.Add("spacer");
                    }
                    bool nextFloatsRight = i + 1 < cells.Count && (cells[i + 1].Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == AnchorStyles.Right;
                    if (stretchH) extra["flex"] = 1;
                    // Width up to the next control reproduces the designer's column alignment...
                    else if (i + 1 < cells.Count && !nextFloatsRight && cells[i + 1].Left > c.Left) extra["width"] = Math.Max(0, cells[i + 1].Left - c.Left - HGap);
                    // ...but auto-sized controls keep their natural size, since their text can change.
                    else if (!c.AutoSize) extra["width"] = Math.Max(0, c.Width);
                    if ((c.Anchor & (AnchorStyles.Top | AnchorStyles.Bottom)) == (AnchorStyles.Top | AnchorStyles.Bottom)) stretchV = true;
                    else if (IsTall(c)) extra["height"] = Math.Max(0, c.Height);
                    hb.Items.Add(c);
                    hb.Extra[c] = extra;
                }
                if (stretchV) hb.Flex = 1;
                hb.Extra[hb] = new Dictionary<string, object> { { "align", stretchV ? "stretch" : "center" } };
                col.Items.Add(hb);
            }
            return col;
        }

        static bool StretchesHorizontally(Control c) =>
            c.Dock == DockStyle.Fill || (c.Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right);

        static bool IsTall(Control c) =>
            (c is TextBoxBase t && t.Multiline) || c is ListBox || c is ListView || c is DataGridView || c is TreeView || c is GroupBox || c is TabControl || c is Panel;

        static Dictionary<string, object> SizeHint(Control c, bool vertical)
        {
            var d = new Dictionary<string, object>();
            if (vertical && IsTall(c) && !(c is GroupBox || c is Panel || c is TabControl)) d["height"] = Math.Max(0, c.Height);
            return d;
        }

        void EmitItems(Box b, string parentId, HashSet<object> live)
        {
            int order = 0, n = 0;
            foreach (var item in b.Items)
            {
                if (item is Box sub) EmitBox(sub, parentId, order++, parentId + "__" + (sub.Horizontal ? "h" : "v") + n++, live);
                else if (item is string) { AddBox(parentId + "__s" + n++, "spacer", parentId, order++).Attrs["flex"] = 1; }
                else
                {
                    Dictionary<string, object> extra;
                    b.Extra.TryGetValue(item, out extra);
                    Element(item, parentId, order++, extra, live);
                }
            }
        }

        void EmitBox(Box b, string parentId, int order, string id, HashSet<object> live)
        {
            var node = AddBox(id, b.Horizontal ? "hbox" : "vbox", parentId, order);
            if (b.Flex != null) node.Attrs["flex"] = b.Flex;
            Dictionary<string, object> extra;
            if (b.Extra.TryGetValue(b, out extra)) foreach (var kv in extra) node.Attrs[kv.Key] = kv.Value;
            EmitItems(b, id, live);
        }

        // ---- widgets -------------------------------------------------------------

        void Element(object o, string parentId, int order, object extraOrFlex, HashSet<object> live)
        {
            var extra = extraOrFlex as Dictionary<string, object>;
            if (extraOrFlex is int) extra = new Dictionary<string, object> { { "flex", extraOrFlex } };
            VNode n = o is ToolStripItem item ? ToolItem(item, parentId, order, live) : Widget((Control)o, parentId, order, live);
            if (n == null) return;
            if (extra != null) foreach (var kv in extra) if (!n.Attrs.ContainsKey(kv.Key)) n.Attrs[kv.Key] = kv.Value;
        }

        VNode Widget(Control c, string parentId, int order, HashSet<object> live)
        {
            VNode n;
            switch (c)
            {
                case StatusStrip ss: return Strip(ss, "statusbar", parentId, order, live);
                case ToolStrip ts: return Strip(ts, "toolbar", parentId, order, live);
                case Button b:
                    n = Add(c, "button", parentId, order, live);
                    n.Attrs["label"] = Mnemonic(b.Text);
                    var form = b.FindForm();
                    if (form != null && form.AcceptButton == b) n.Attrs["class"] = "primary";
                    Command(n, c.Enabled, KeyFor(b.Text, form != null && form.CancelButton == b ? "escape" : null));
                    break;
                case CheckBox cb:
                    n = Add(c, "checkbox", parentId, order, live);
                    n.Attrs["label"] = Mnemonic(cb.Text);
                    n.Attrs["value"] = cb.Checked;
                    break;
                case RadioButton rb:
                    n = Add(c, "checkbox", parentId, order, live);
                    n.Attrs["label"] = Mnemonic(rb.Text);
                    n.Attrs["value"] = rb.Checked;
                    break;
                case TextBoxBase tb:
                    n = Add(c, "textbox", parentId, order, live);
                    n.Attrs["value"] = tb.Text;
                    if (tb.ReadOnly) n.Attrs["disabled"] = true;
                    if (tb.Multiline) n.Attrs["multiline"] = true;
                    if (tb is TextBox t && (t.PasswordChar != '\0' || t.UseSystemPasswordChar)) n.Attrs["password"] = true;
                    break;
                case NumericUpDown nud:
                    n = Add(c, "textbox", parentId, order, live);
                    n.Attrs["value"] = nud.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case DateTimePicker dtp:
                    n = Add(c, "textbox", parentId, order, live);
                    n.Attrs["value"] = dtp.Text;
                    n.Attrs["disabled"] = true;
                    break;
                case ComboBox combo:
                    n = Add(c, "menulist", parentId, order, live);
                    n.Attrs["options"] = combo.Items.Cast<object>().Select((it, i) => new Dictionary<string, object> {
                        { "value", i.ToString() }, { "label", combo.GetItemText(it) } }).ToList();
                    if (combo.SelectedIndex >= 0) n.Attrs["selectedIndex"] = combo.SelectedIndex;
                    break;
                case ProgressBar pb:
                    n = Add(c, "progressmeter", parentId, order, live);
                    n.Attrs["value"] = pb.Maximum > pb.Minimum ? (double)(pb.Value - pb.Minimum) / (pb.Maximum - pb.Minimum) : 0.0;
                    break;
                case TrackBar tr:
                    n = Add(c, "progressmeter", parentId, order, live);
                    n.Attrs["value"] = tr.Maximum > tr.Minimum ? (double)(tr.Value - tr.Minimum) / (tr.Maximum - tr.Minimum) : 0.0;
                    break;
                case FilePicker fp:
                    n = Add(c, "filepicker", parentId, order, live);
                    if (fp.Accept != null) n.Attrs["accept"] = fp.Accept;
                    if (fp.Multiple) n.Attrs["multiple"] = true;
                    if (fp.Files.Count > 0) n.Attrs["value"] = string.Join(", ", fp.Files.Select(System.IO.Path.GetFileName));
                    break;
                case DataGridView grid: n = Grid(grid, parentId, order, live); break;
                case ListView lv: n = ListViewTree(lv, parentId, order, live); break;
                case ListBox lb:
                    n = Add(c, "tree", parentId, order, live);
                    n.Attrs["cols"] = new List<object> { Col("c0", "Items", null) };
                    n.Rows = lb.Items.Cast<object>().Select(it => new Dictionary<string, object> { { "c0", lb.GetItemText(it) } }).ToList();
                    break;
                case Label l:
                    n = Add(c, "label", parentId, order, live);
                    n.Attrs["value"] = Mnemonic(l.Text);
                    break;
                case TabControl tabs:
                    n = Add(c, "tabbox", parentId, order, live);
                    int p = 0;
                    foreach (TabPage page in tabs.TabPages)
                    {
                        var panel = Add(page, "tabpanel", n.Id, p++, live);
                        panel.Attrs["label"] = page.Text;
                        panel.Attrs["flex"] = 1;
                        LayoutChildren(page, panel.Id, live);
                    }
                    break;
                case SplitContainer split:
                    n = Add(c, split.Orientation == Orientation.Vertical ? "hbox" : "vbox", parentId, order, live);
                    n.Attrs["align"] = "stretch";
                    var p1 = Add(split.Panel1, "vbox", n.Id, 0, live);
                    p1.Attrs[split.Orientation == Orientation.Vertical ? "width" : "height"] = split.SplitterDistance;
                    LayoutChildren(split.Panel1, p1.Id, live);
                    var p2 = Add(split.Panel2, "vbox", n.Id, 1, live);
                    p2.Attrs["flex"] = 1;
                    LayoutChildren(split.Panel2, p2.Id, live);
                    break;
                case GroupBox gb:
                    n = Add(c, "groupbox", parentId, order, live);
                    n.Attrs["label"] = Mnemonic(gb.Text);
                    LayoutChildren(c, n.Id, live);
                    break;
                case FlowLayoutPanel flp:
                    n = Add(c, flp.FlowDirection == FlowDirection.LeftToRight || flp.FlowDirection == FlowDirection.RightToLeft ? "hbox" : "vbox", parentId, order, live);
                    LayoutChildren(c, n.Id, live);
                    break;
                default:
                    if (c.Controls.Count > 0 || c is Panel || c is UserControl)
                    {
                        n = Add(c, "vbox", parentId, order, live);
                        LayoutChildren(c, n.Id, live);
                    }
                    else
                    {
                        n = Add(c, "description", parentId, order, live);
                        n.Attrs["value"] = "[" + c.GetType().Name + "]";
                        n.Attrs["class"] = "muted";
                    }
                    break;
            }
            if (!c.Enabled && !n.Attrs.ContainsKey("command")) n.Attrs["disabled"] = true;
            if (!c.Visible && (c.Parent == null || c.Parent.Visible)) n.Attrs["hidden"] = true;
            return n;
        }

        VNode Grid(DataGridView grid, string parentId, int order, HashSet<object> live)
        {
            var n = Add(grid, "tree", parentId, order, live);
            var cols = grid.Columns.Cast<DataGridViewColumn>().Where(col => col.Visible).OrderBy(col => col.DisplayIndex).ToList();
            n.Attrs["cols"] = cols.Select(col => (object)Col("c" + col.Index, col.HeaderText,
                col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? (int?)null : col.Width)).ToList();
            n.Rows = new List<Dictionary<string, object>>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var r = new Dictionary<string, object>();
                foreach (var col in cols) r["c" + col.Index] = Convert.ToString(row.Cells[col.Index].FormattedValue) ?? "";
                n.Rows.Add(r);
            }
            n.Attrs["class"] = "mono";
            return n;
        }

        VNode ListViewTree(ListView lv, string parentId, int order, HashSet<object> live)
        {
            var n = Add(lv, "tree", parentId, order, live);
            int count = Math.Max(1, lv.Columns.Count);
            n.Attrs["cols"] = Enumerable.Range(0, count).Select(i => (object)Col("c" + i,
                i < lv.Columns.Count ? lv.Columns[i].Text : "Items", i < lv.Columns.Count && i < count - 1 ? (int?)lv.Columns[i].Width : null)).ToList();
            n.Rows = lv.Items.Cast<ListViewItem>().Select(it =>
            {
                var r = new Dictionary<string, object>();
                for (int i = 0; i < count; i++) r["c" + i] = i < it.SubItems.Count ? it.SubItems[i].Text : "";
                return r;
            }).ToList();
            return n;
        }

        static Dictionary<string, object> Col(string id, string label, int? width)
        {
            var d = new Dictionary<string, object> { { "id", id }, { "label", label ?? "" } };
            if (width.HasValue) d["width"] = Math.Max(20, width.Value);
            else d["flex"] = 1;
            return d;
        }

        // ---- tool strips -----------------------------------------------------------

        VNode Strip(ToolStrip ts, string tag, string parentId, int order, HashSet<object> live)
        {
            var n = Add(ts, tag, parentId, order, live);
            int i = 0;
            foreach (ToolStripItem item in ts.Items) ToolItemTree(item, n.Id, ref i, "", live);
            if (!ts.Visible && (ts.Parent == null || ts.Parent.Visible)) n.Attrs["hidden"] = true;
            return n;
        }

        // Menus have no XUL-J widget yet: leaf menu items flatten into buttons labelled "File › Exit".
        void ToolItemTree(ToolStripItem item, string parentId, ref int order, string path, HashSet<object> live)
        {
            if (item is ToolStripDropDownItem dd && dd.DropDownItems.Count > 0)
            {
                foreach (ToolStripItem child in dd.DropDownItems)
                    ToolItemTree(child, parentId, ref order, path + Mnemonic(dd.Text) + " › ", live);
                return;
            }
            var n = ToolItem(item, parentId, order, live, path);
            if (n != null) order++;
        }

        VNode ToolItem(ToolStripItem item, string parentId, int order, HashSet<object> live, string path = "")
        {
            VNode n;
            switch (item)
            {
                case ToolStripSeparator _: return null;
                case ToolStripStatusLabel sl:
                    n = Add(item, "label", parentId, order, live);
                    n.Attrs["value"] = sl.Text ?? "";
                    if (sl.Spring) n.Attrs["flex"] = 1;
                    break;
                case ToolStripLabel l:
                    n = Add(item, "label", parentId, order, live);
                    n.Attrs["value"] = Mnemonic(l.Text);
                    break;
                case ToolStripProgressBar pb:
                    n = Add(item, "progressmeter", parentId, order, live);
                    n.Attrs["value"] = pb.Maximum > pb.Minimum ? (double)(pb.Value - pb.Minimum) / (pb.Maximum - pb.Minimum) : 0.0;
                    break;
                case ToolStripComboBox tc:
                    hostedBy[tc.ComboBox] = tc;
                    return Widget(tc.ComboBox, parentId, order, live);
                case ToolStripTextBox tt:
                    hostedBy[tt.TextBox] = tt;
                    return Widget(tt.TextBox, parentId, order, live);
                default:
                    n = Add(item, "toolbarbutton", parentId, order, live);
                    n.Attrs["label"] = path + Mnemonic(item.Text);
                    string key = null;
                    if (item is ToolStripMenuItem mi && mi.ShortcutKeys != Keys.None) key = KeyName(mi.ShortcutKeys);
                    Command(n, item.Enabled, key);
                    break;
            }
            if (!item.Available) n.Attrs["hidden"] = true;
            return n;
        }

        // ---- commands and keys -------------------------------------------------------

        void Command(VNode n, bool enabled, string key)
        {
            string id = "cmd_" + n.Id;
            n.Attrs["command"] = id;
            var state = new Dictionary<string, object> { { "disabled", !enabled } };
            if (key != null && Regex.IsMatch(key, "^((ctrl|alt|shift|meta)\\+)*[a-z0-9]+$")) state["key"] = key;
            commands[id] = state;
        }

        static string KeyFor(string text, string fallback)
        {
            var m = Regex.Match(text ?? "", "(?<!&)&([A-Za-z0-9])");
            return m.Success ? "alt+" + m.Groups[1].Value.ToLowerInvariant() : fallback;
        }

        static string KeyName(Keys keys)
        {
            var sb = new StringBuilder();
            if ((keys & Keys.Control) != 0) sb.Append("ctrl+");
            if ((keys & Keys.Alt) != 0) sb.Append("alt+");
            if ((keys & Keys.Shift) != 0) sb.Append("shift+");
            var k = keys & Keys.KeyCode;
            string name = k >= Keys.D0 && k <= Keys.D9 ? ((int)(k - Keys.D0)).ToString() : k.ToString().ToLowerInvariant();
            if (k == Keys.Return) name = "enter";
            return sb.Append(name).ToString();
        }

        static string Mnemonic(string text) => Regex.Replace(text ?? "", "&(&?)", "$1");
    }
}
