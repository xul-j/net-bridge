// Diffs successive virtual trees from Renderer and produces the minimal XUL-J ops.
// Op order matters for the client: removes, then updates (so `order` is current), then
// new nodes in pre-order (parents before children), then row data.
using System.Collections.Generic;
using System.Linq;

namespace XulJ.Bridge
{
    public sealed class Reconciler
    {
        Dictionary<string, VNode> prev = new Dictionary<string, VNode>();
        Dictionary<string, string> prevAttrs = new Dictionary<string, string>();
        Dictionary<string, string> prevCommands = new Dictionary<string, string>();
        Dictionary<string, List<string>> prevRows = new Dictionary<string, List<string>>();

        public List<Dictionary<string, object>> Diff(List<VNode> nodes, Dictionary<string, Dictionary<string, object>> commands)
        {
            var ops = new List<Dictionary<string, object>>();
            var next = new Dictionary<string, VNode>();
            foreach (var n in nodes) next[n.Id] = n;

            // Commands first: widgets that reference them render with the right state.
            var nextCommands = new Dictionary<string, string>();
            foreach (var kv in commands)
            {
                string json = Json.Write(kv.Value);
                nextCommands[kv.Key] = json;
                string old;
                if (prevCommands.TryGetValue(kv.Key, out old) && old == json) continue;
                var op = new Dictionary<string, object> { { "op", "command" }, { "id", kv.Key } };
                foreach (var a in kv.Value) op[a.Key] = a.Value;
                ops.Add(op);
            }
            foreach (var id in prevCommands.Keys.Where(k => !nextCommands.ContainsKey(k)))
                ops.Add(new Dictionary<string, object> { { "op", "command" }, { "id", id }, { "deleted", true } });

            // Removals: gone, or moved to another parent (re-inserted below).
            var removed = new HashSet<string>();
            foreach (var old in prev.Values)
            {
                VNode now;
                if (!next.TryGetValue(old.Id, out now) || now.Parent != old.Parent) removed.Add(old.Id);
            }
            foreach (var id in removed)
            {
                // Removing a parent removes its subtree on the client; skip descendants.
                if (prev[id].Parent != null && removed.Contains(prev[id].Parent)) continue;
                ops.Add(new Dictionary<string, object> { { "op", "remove" }, { "id", id } });
            }

            var nextAttrs = new Dictionary<string, string>();
            var inserts = new List<Dictionary<string, object>>();
            foreach (var n in nodes)
            {
                var attrs = new Dictionary<string, object>(n.Attrs) { ["order"] = n.Order };
                if (n.Tag == "tree") attrs["rows"] = new Dictionary<string, object> { { "source", n.Id } };
                string json = Json.Write(attrs);
                nextAttrs[n.Id] = json;
                VNode old;
                bool existed = prev.TryGetValue(n.Id, out old) && !removed.Contains(n.Id) && !AncestorRemoved(old, removed);
                if (!existed)
                {
                    var op = new Dictionary<string, object> { { "op", "node" }, { "in", n.Parent }, { "id", n.Id }, { "tag", n.Tag } };
                    foreach (var a in attrs) op[a.Key] = a.Value;
                    inserts.Add(op);
                }
                else if (old.Tag != n.Tag)
                {
                    var op = new Dictionary<string, object> { { "op", "replace" }, { "id", n.Id }, { "tag", n.Tag } };
                    foreach (var a in attrs) op[a.Key] = a.Value;
                    ops.Add(op);
                }
                else if (prevAttrs[n.Id] != json)
                {
                    ops.Add(new Dictionary<string, object> { { "op", "set" }, { "id", n.Id }, { "attrs", attrs }, { "replace", true } });
                }
            }
            ops.AddRange(inserts);

            // Row data: append when the old rows are a prefix of the new ones, else resend.
            var nextRows = new Dictionary<string, List<string>>();
            foreach (var n in nodes.Where(x => x.Rows != null))
            {
                var rows = n.Rows.Select(r => Json.Write(r)).ToList();
                nextRows[n.Id] = rows;
                List<string> old;
                bool fresh = !prevRows.TryGetValue(n.Id, out old) || removed.Contains(n.Id);
                if (!fresh && old.Count <= rows.Count && Enumerable.Range(0, old.Count).All(i => old[i] == rows[i]))
                {
                    if (rows.Count > old.Count)
                        ops.Add(new Dictionary<string, object> { { "op", "rows" }, { "source", n.Id }, { "append", n.Rows.Skip(old.Count).ToList() } });
                }
                else
                {
                    ops.Add(new Dictionary<string, object> { { "op", "rows" }, { "source", n.Id }, { "clear", true }, { "append", n.Rows } });
                }
            }
            // A tree that disappears leaves its source behind; clear it so a later tree starts empty.
            foreach (var id in prevRows.Keys.Where(k => !nextRows.ContainsKey(k)))
                ops.Add(new Dictionary<string, object> { { "op", "rows" }, { "source", id }, { "clear", true } });

            prev = next;
            prevAttrs = nextAttrs;
            prevCommands = nextCommands;
            prevRows = nextRows;
            return ops;
        }

        bool AncestorRemoved(VNode n, HashSet<string> removed)
        {
            for (var p = n.Parent; p != null && prev.ContainsKey(p); p = prev[p].Parent)
                if (removed.Contains(p)) return true;
            return false;
        }
    }
}
