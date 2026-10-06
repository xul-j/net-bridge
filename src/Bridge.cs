// XUL-J bridge: hosts a WinForms application as a web server. Each browser session gets its
// own instance of the app's form, kept off-screen; the bridge renders it to XUL-J ops over SSE
// and replays browser intents as clicks and edits on the real controls.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace XulJ.Bridge
{
    public sealed class BridgeOptions
    {
        public int Port = 8092;
        public string Host = "*";
        public int TickMs = 50;
        public TimeSpan SessionTtl = TimeSpan.FromMinutes(10);
        public TextWriter Log = Console.Out;
    }

    public static class XulJBridge
    {
        /// <summary>Use instead of Application.Run(new MainForm()) to serve the app to browsers.</summary>
        public static void Run(Func<Form> createForm, BridgeOptions options = null)
        {
            new Host(createForm, options ?? new BridgeOptions()).Run();
        }
    }

    sealed class Session
    {
        public readonly string Id;
        public readonly List<Form> Forms = new List<Form>();
        public readonly Renderer Renderer = new Renderer();
        public readonly Reconciler Reconciler = new Reconciler();
        public readonly object Sync = new object();
        public readonly List<string> Log = new List<string>();
        public readonly List<BlockingCollection<string>> Subscribers = new List<BlockingCollection<string>>();
        public int Seq;
        public DateTime LastSeen = DateTime.UtcNow;
        public bool Ended;

        public Session(string id) { Id = id; }

        public void Emit(Dictionary<string, object> op)
        {
            lock (Sync)
            {
                op["seq"] = ++Seq;
                string frame = "id: " + Seq + "\ndata: " + Json.Write(op) + "\n\n";
                Log.Add(frame);
                foreach (var s in Subscribers) s.TryAdd(frame);
            }
        }
    }

    sealed class Host
    {
        static readonly MethodInfo OnClick = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly Func<Form> createForm;
        readonly BridgeOptions opt;
        readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();
        readonly HashSet<Form> owned = new HashSet<Form>();
        Control ui; // marshals work onto the UI thread
        Session lastActive;

        public Host(Func<Form> createForm, BridgeOptions opt)
        {
            this.createForm = createForm;
            this.opt = opt;
        }

        public void Run()
        {
            ui = new Control();
            ui.CreateControl();
            var timer = new System.Windows.Forms.Timer { Interval = opt.TickMs };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Application.ThreadException += (s, e) => opt.Log.WriteLine("app exception: " + e.Exception);

            var listener = new HttpListener();
            listener.Prefixes.Add("http://" + opt.Host + ":" + opt.Port + "/");
            listener.Start();
            opt.Log.WriteLine("XUL-J bridge serving on http://" + opt.Host + ":" + opt.Port + "/");
            var accept = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); } catch { break; }
                    ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                }
            }) { IsBackground = true, Name = "xulj-http" };
            accept.Start();
            Application.Run();
        }

        // ---- UI thread -----------------------------------------------------------

        void Tick()
        {
            // Forms opened by the app (dialogs, extra windows) belong to the session that acted last.
            foreach (var f in Application.OpenForms.Cast<Form>().ToList())
            {
                if (owned.Contains(f) || lastActive == null || lastActive.Ended) continue;
                owned.Add(f);
                lastActive.Forms.Add(f);
                HideOffscreen(f);
            }
            foreach (var s in sessions.Values.ToList())
            {
                if (s.Ended) continue;
                bool idle;
                lock (s.Sync) idle = s.Subscribers.Count == 0 && DateTime.UtcNow - s.LastSeen > opt.SessionTtl;
                if (idle) { End(s); continue; }
                // Closed dialogs are only hidden (ShowDialog never disposes); release them so a
                // reused dialog instance is picked up again the next time it opens.
                foreach (var f in s.Forms.Skip(1).Where(f => f.IsDisposed || !f.Visible).ToList()) { s.Forms.Remove(f); owned.Remove(f); }
                if (s.Forms.Count > 0 && s.Forms[0].IsDisposed) s.Forms.RemoveAt(0);
                if (s.Forms.Count == 0)
                {
                    s.Ended = true;
                    s.Emit(new Dictionary<string, object> { { "op", "reset" } });
                    s.Emit(new Dictionary<string, object> { { "op", "node" }, { "in", "root" }, { "tag", "description" }, { "id", "ended" },
                        { "value", "The application closed this window. Reload to start a new session." }, { "class", "muted" } });
                    continue;
                }
                try
                {
                    s.Renderer.Render(s.Forms);
                    foreach (var op in s.Reconciler.Diff(s.Renderer.Nodes, s.Renderer.Commands)) s.Emit(op);
                }
                catch (Exception e) { opt.Log.WriteLine("render failed: " + e); }
            }
        }

        static void HideOffscreen(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-32000, -32000);
            f.ShowInTaskbar = false;
        }

        Session CreateSession(string id)
        {
            var s = new Session(id);
            s.Emit(new Dictionary<string, object> { { "op", "reset" } });
            var form = createForm();
            HideOffscreen(form);
            owned.Add(form);
            s.Forms.Add(form);
            form.Show(); // fires Load, starts the app's timers
            lastActive = s;
            return s;
        }

        void End(Session s)
        {
            s.Ended = true;
            sessions.TryRemove(s.Id, out _);
            foreach (var f in s.Forms.ToList()) { try { f.Close(); f.Dispose(); } catch { } owned.Remove(f); }
            opt.Log.WriteLine("session " + s.Id + " expired");
        }

        // Validates an intent synchronously (so the HTTP reply can say 404/409) and returns the
        // action to run afterwards. Running it asynchronously keeps modal dialogs from blocking HTTP.
        int Prepare(Session s, Dictionary<string, object> msg, out Action action)
        {
            action = null;
            string op = msg.TryGetValue("op", out var o) ? o as string : null;
            if (op == "do")
            {
                string cmd = msg.TryGetValue("command", out var c) ? c as string : null;
                if (cmd == null || !cmd.StartsWith("cmd_")) return 400;
                var target = s.Renderer.Lookup(cmd.Substring(4));
                if (target == null || !s.Renderer.Commands.ContainsKey(cmd)) return 404;
                if (true.Equals(s.Renderer.Commands[cmd]["disabled"])) return 409;
                if (target is ToolStripItem item) action = () => item.PerformClick();
                else if (target is Control ctl) action = () => OnClick.Invoke(ctl, new object[] { EventArgs.Empty });
                else return 404;
                return 202;
            }
            if (op == "input")
            {
                string id = msg.TryGetValue("id", out var i) ? i as string : null;
                msg.TryGetValue("value", out var value);
                var target = id == null ? null : s.Renderer.Lookup(id) as Control;
                if (target == null) return 404;
                if (!target.Enabled) return 409;
                switch (target)
                {
                    case TextBoxBase tb when !tb.ReadOnly: action = () => tb.Text = Convert.ToString(value); break;
                    case CheckBox cb: action = () => cb.Checked = true.Equals(value); break;
                    case RadioButton rb: action = () => { if (true.Equals(value)) rb.Checked = true; }; break;
                    case ComboBox combo:
                        if (!int.TryParse(Convert.ToString(value), out var idx) || idx < 0 || idx >= combo.Items.Count) return 400;
                        action = () => combo.SelectedIndex = idx;
                        break;
                    case NumericUpDown nud:
                        if (!decimal.TryParse(Convert.ToString(value), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)) return 400;
                        action = () => nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, d));
                        break;
                    default: return 409;
                }
                return 202;
            }
            return 400;
        }

        // ---- HTTP (pool threads) ----------------------------------------------------

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                string path = req.Url.AbsolutePath;
                if (req.HttpMethod == "GET" && path == "/stream") Stream(ctx);
                else if (req.HttpMethod == "POST" && path == "/intent") Intent(ctx);
                else if (req.HttpMethod == "GET") Static(res, path == "/" ? "/index.html" : path);
                else Text(res, 405, "method not allowed");
            }
            catch (Exception e)
            {
                opt.Log.WriteLine("http error: " + e.Message);
                try { res.Abort(); } catch { }
            }
        }

        static bool ValidSessionId(string id) => id != null && Regex.IsMatch(id, "^[A-Za-z0-9_-]{4,64}$");

        void Stream(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            string id = ctx.Request.QueryString["session"];
            if (!ValidSessionId(id)) { Text(res, 400, "bad session"); return; }
            Session s;
            lock (sessions)
            {
                // Not GetOrAdd: its factory can run twice and would leave an orphaned form.
                if (!sessions.TryGetValue(id, out s) || s.Ended)
                {
                    s = (Session)ui.Invoke(new Func<Session>(() => CreateSession(id)));
                    sessions[id] = s;
                }
            }
            int from = 0;
            int.TryParse(ctx.Request.Headers["Last-Event-ID"] ?? ctx.Request.QueryString["from"], out from);

            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["X-Accel-Buffering"] = "no";
            res.SendChunked = true;
            var queue = new BlockingCollection<string>();
            var output = res.OutputStream;
            lock (s.Sync)
            {
                int start = from > s.Seq ? 0 : from;
                queue.Add("retry: 1000\n\n");
                foreach (var frame in s.Log.Skip(start)) queue.Add(frame);
                s.Subscribers.Add(queue);
            }
            try
            {
                while (true)
                {
                    string frame;
                    if (!queue.TryTake(out frame, 15000)) frame = ": ping\n\n";
                    var bytes = Encoding.UTF8.GetBytes(frame);
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
            }
            catch { /* client went away */ }
            finally
            {
                lock (s.Sync) { s.Subscribers.Remove(queue); s.LastSeen = DateTime.UtcNow; }
                try { res.Close(); } catch { }
            }
        }

        void Intent(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            Session s;
            if (!sessions.TryGetValue(ctx.Request.QueryString["session"] ?? "", out s) || s.Ended) { Text(res, 404, "no such session"); return; }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            Dictionary<string, object> msg;
            try { msg = Json.Parse(body) as Dictionary<string, object>; } catch { msg = null; }
            if (msg == null || body.Length > 65536) { Text(res, 400, "bad json"); return; }
            Action action = null;
            int status = (int)ui.Invoke(new Func<int>(() => Prepare(s, msg, out action)));
            if (status == 202)
            {
                ui.BeginInvoke(new Action(() =>
                {
                    lastActive = s;
                    try { action(); }
                    catch (TargetInvocationException e) { opt.Log.WriteLine("app handler failed: " + e.InnerException); }
                    catch (Exception e) { opt.Log.WriteLine("app handler failed: " + e); }
                }));
            }
            lock (s.Sync) s.LastSeen = DateTime.UtcNow;
            Text(res, status, status == 202 ? "ok" : status == 409 ? "command disabled" : status == 404 ? "unknown target" : "bad intent");
        }

        static readonly Dictionary<string, string> Types = new Dictionary<string, string>
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".json", "application/json" },
        };

        static void Static(HttpListenerResponse res, string path)
        {
            if (!Regex.IsMatch(path, "^/[A-Za-z0-9_.-]+$")) { Text(res, 404, "not found"); return; }
            var stream = typeof(Host).Assembly.GetManifestResourceStream("public" + path);
            if (stream == null) { Text(res, 404, "not found"); return; }
            string type;
            res.ContentType = Types.TryGetValue(Path.GetExtension(path), out type) ? type : "application/octet-stream";
            using (stream) stream.CopyTo(res.OutputStream);
            res.Close();
        }

        static void Text(HttpListenerResponse res, int code, string text)
        {
            res.StatusCode = code;
            res.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(text);
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }
    }
}
