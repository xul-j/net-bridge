// Browser-friendly replacements for native dialogs. On Windows, MessageBox and the common
// dialogs are native windows the bridge cannot see or drive, so they are patched (Harmony) to
// show ordinary managed forms instead, which render as XUL-J modal windows:
//   MessageBox.Show            → BridgeDialog with the same buttons, icon and default button
//   OpenFileDialog             → a file picker: the browser uploads, the app gets a temp path
//   SaveFileDialog             → the app writes to a temp path, the browser downloads the file
//   Interaction.InputBox (VB)  → a prompt with a text box
//   other CommonDialogs        → cancelled, with a notification (no browser equivalent)
// Calls on any thread other than the bridge's UI thread run the original implementation.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;

namespace XulJ.Bridge
{
    /// <summary>A modal form created by the bridge; rendered with its message and icon.</summary>
    public class BridgeDialog : Form
    {
        public string Message;
        public string IconKind; // info, warning, error, question or null
        readonly List<Button> buttons = new List<Button>();
        int nextTop = 12;

        public BridgeDialog(string title, string message, string icon)
        {
            Text = string.IsNullOrEmpty(title) ? "Message" : title;
            Message = message ?? "";
            IconKind = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ClientSize = new Size(420, 120);
        }

        public void AddRow(params Control[] controls)
        {
            int left = 12;
            foreach (var c in controls)
            {
                c.Location = new Point(left, nextTop);
                left += c.Width + 8;
                Controls.Add(c);
            }
            nextTop += 30;
        }

        public Button AddButton(string text, DialogResult result)
        {
            var b = new Button { Text = text, DialogResult = result, Name = "dlg" + result, Size = new Size(80, 25) };
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            buttons.Add(b);
            Controls.Add(b);
            // Right-aligned row along the bottom, in the order given.
            int left = ClientSize.Width - 12 - buttons.Count * 88 + 8;
            for (int i = 0; i < buttons.Count; i++) buttons[i].Location = new Point(left + i * 88, ClientSize.Height - 37);
            return b;
        }
    }

    /// <summary>A control the bridge renders as a browser file picker; uploads land in Files.</summary>
    public class FilePicker : Control
    {
        public string Accept;
        public bool Multiple;
        public readonly List<string> Files = new List<string>();
        public event EventHandler FilesChanged;

        public void AddFile(string path)
        {
            if (!Multiple) Files.Clear();
            Files.Add(path);
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    static class Dialogs
    {
        static Host host;

        public static void Install(Host h)
        {
            host = h;
            var harmony = new Harmony("xulj.bridge.dialogs");
            var mb = new HarmonyMethod(typeof(Dialogs).GetMethod(nameof(MessageBoxPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var m in typeof(MessageBox).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Show"))
                harmony.Patch(m, prefix: mb);
            var cd = new HarmonyMethod(typeof(Dialogs).GetMethod(nameof(CommonDialogPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var m in typeof(CommonDialog).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "ShowDialog"))
                harmony.Patch(m, prefix: cd);
            // Microsoft.VisualBasic is usually loaded only when the app first touches it.
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) => PatchInputBox(harmony, e.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) PatchInputBox(harmony, asm);
        }

        static bool inputBoxPatched;

        static void PatchInputBox(Harmony harmony, Assembly asm)
        {
            if (inputBoxPatched || asm.GetName().Name != "Microsoft.VisualBasic") return;
            var input = asm.GetType("Microsoft.VisualBasic.Interaction")?.GetMethod("InputBox", BindingFlags.Public | BindingFlags.Static);
            if (input == null) return;
            inputBoxPatched = true;
            harmony.Patch(input, prefix: new HarmonyMethod(typeof(Dialogs).GetMethod(nameof(InputBoxPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static object Arg(MethodBase m, object[] args, string name)
        {
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++) if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase)) return args[i];
            return null;
        }

        static IWin32Window OwnerOf(MethodBase m, object[] args) => Arg(m, args, "owner") as IWin32Window ?? host.ActiveForm();

        // ---- MessageBox ----------------------------------------------------------------

        static bool MessageBoxPrefix(object[] __args, MethodBase __originalMethod, ref DialogResult __result)
        {
            if (host == null || !host.OnUiThread) return true;
            string text = Arg(__originalMethod, __args, "text") as string;
            string caption = Arg(__originalMethod, __args, "caption") as string;
            var buttons = Arg(__originalMethod, __args, "buttons") is MessageBoxButtons b ? b : MessageBoxButtons.OK;
            var icon = Arg(__originalMethod, __args, "icon") is MessageBoxIcon i ? i : MessageBoxIcon.None;
            var def = Arg(__originalMethod, __args, "defaultButton") is MessageBoxDefaultButton d ? d : MessageBoxDefaultButton.Button1;

            using (var dlg = new BridgeDialog(caption, text, IconName(icon)))
            {
                var list = new List<Button>();
                switch (buttons)
                {
                    case MessageBoxButtons.OKCancel:
                        list.Add(dlg.AddButton("OK", DialogResult.OK));
                        dlg.CancelButton = AddTo(list, dlg.AddButton("Cancel", DialogResult.Cancel));
                        break;
                    case MessageBoxButtons.AbortRetryIgnore:
                        list.Add(dlg.AddButton("Abort", DialogResult.Abort));
                        list.Add(dlg.AddButton("Retry", DialogResult.Retry));
                        list.Add(dlg.AddButton("Ignore", DialogResult.Ignore));
                        break;
                    case MessageBoxButtons.YesNoCancel:
                        list.Add(dlg.AddButton("Yes", DialogResult.Yes));
                        list.Add(dlg.AddButton("No", DialogResult.No));
                        dlg.CancelButton = AddTo(list, dlg.AddButton("Cancel", DialogResult.Cancel));
                        break;
                    case MessageBoxButtons.YesNo:
                        list.Add(dlg.AddButton("Yes", DialogResult.Yes));
                        list.Add(dlg.AddButton("No", DialogResult.No));
                        break;
                    case MessageBoxButtons.RetryCancel:
                        list.Add(dlg.AddButton("Retry", DialogResult.Retry));
                        dlg.CancelButton = AddTo(list, dlg.AddButton("Cancel", DialogResult.Cancel));
                        break;
                    default:
                        dlg.CancelButton = AddTo(list, dlg.AddButton("OK", DialogResult.OK));
                        break;
                }
                int idx = def == MessageBoxDefaultButton.Button2 ? 1 : def == MessageBoxDefaultButton.Button3 ? 2 : 0;
                dlg.AcceptButton = list[Math.Min(idx, list.Count - 1)];
                __result = dlg.ShowDialog(OwnerOf(__originalMethod, __args));
            }
            return false;
        }

        static Button AddTo(List<Button> list, Button b) { list.Add(b); return b; }

        static string IconName(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Information: return "info"; // also Asterisk
                case MessageBoxIcon.Warning: return "warning";  // also Exclamation
                case MessageBoxIcon.Error: return "error";      // also Hand, Stop
                case MessageBoxIcon.Question: return "question";
                default: return null;
            }
        }

        // ---- common dialogs --------------------------------------------------------------

        static bool CommonDialogPrefix(CommonDialog __instance, object[] __args, MethodBase __originalMethod, ref DialogResult __result)
        {
            if (host == null || !host.OnUiThread) return true;
            var owner = __args.Length > 0 ? __args[0] as IWin32Window ?? host.ActiveForm() : host.ActiveForm();
            switch (__instance)
            {
                case OpenFileDialog open: __result = ShowOpen(open, owner); break;
                case SaveFileDialog save: __result = ShowSave(save, owner); break;
                default:
                    host.Notify("“" + __instance.GetType().Name + "” is not available in the browser; it was cancelled.", "warning");
                    __result = DialogResult.Cancel;
                    break;
            }
            return false;
        }

        static DialogResult ShowOpen(OpenFileDialog open, IWin32Window owner)
        {
            using (var dlg = new BridgeDialog(string.IsNullOrEmpty(open.Title) ? "Open" : open.Title,
                "Choose " + (open.Multiselect ? "files" : "a file") + " from your computer to upload to the application.", null))
            {
                var picker = new FilePicker { Name = "picker", Accept = AcceptFrom(open.Filter), Multiple = open.Multiselect, Size = new Size(396, 24) };
                dlg.AddRow(picker);
                var ok = dlg.AddButton("Open", DialogResult.OK);
                ok.Enabled = false;
                dlg.CancelButton = dlg.AddButton("Cancel", DialogResult.Cancel);
                dlg.AcceptButton = ok;
                picker.FilesChanged += (s, e) => ok.Enabled = picker.Files.Count > 0;
                var result = dlg.ShowDialog(owner);
                if (result == DialogResult.OK && picker.Files.Count > 0)
                {
                    open.FileName = picker.Files[0];
                    SetFileNames(open, picker.Files.ToArray());
                }
                return result;
            }
        }

        // FileNames has no setter; the backing field differs between .NET Framework and Mono.
        static void SetFileNames(FileDialog dialog, string[] names)
        {
            foreach (var field in new[] { "fileNames", "file_names" })
            {
                var f = typeof(FileDialog).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(string[])) { f.SetValue(dialog, names); return; }
            }
        }

        static string AcceptFrom(string filter)
        {
            if (string.IsNullOrEmpty(filter)) return null;
            var parts = filter.Split('|');
            if (parts.Length < 2) return null;
            var patterns = parts[1].Split(';').Select(p => p.Trim()).ToList();
            if (patterns.Any(p => p == "*.*" || p == "*")) return null;
            var exts = patterns.Where(p => p.StartsWith("*.")).Select(p => p.Substring(1)).ToList();
            return exts.Count > 0 ? string.Join(",", exts) : null;
        }

        static DialogResult ShowSave(SaveFileDialog save, IWin32Window owner)
        {
            string name = Path.GetFileName(save.FileName ?? "");
            if (name.Length == 0) name = "untitled" + (string.IsNullOrEmpty(save.DefaultExt) ? "" : "." + save.DefaultExt.TrimStart('.'));
            using (var dlg = new BridgeDialog(string.IsNullOrEmpty(save.Title) ? "Save as" : save.Title,
                "The file will be downloaded by your browser.", null))
            {
                var label = new Label { Text = "File name:", Name = "nameLabel", AutoSize = true };
                var box = new TextBox { Text = name, Name = "fileName", Size = new Size(300, 20) };
                dlg.AddRow(label, box);
                var ok = dlg.AddButton("Save", DialogResult.OK);
                dlg.CancelButton = dlg.AddButton("Cancel", DialogResult.Cancel);
                dlg.AcceptButton = ok;
                box.TextChanged += (s, e) => ok.Enabled = SafeName(box.Text).Length > 0;
                var result = dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return result;
                string chosen = SafeName(box.Text);
                if (Path.GetExtension(chosen).Length == 0 && save.AddExtension && !string.IsNullOrEmpty(save.DefaultExt))
                    chosen += "." + save.DefaultExt.TrimStart('.');
                save.FileName = host.ExpectDownload(chosen);
                return result;
            }
        }

        public static string SafeName(string name)
        {
            name = Path.GetFileName((name ?? "").Trim());
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            return name.Trim('.', ' ');
        }

        // ---- Microsoft.VisualBasic.Interaction.InputBox ------------------------------------

        // Positional (Prompt, Title, DefaultResponse, XPos, YPos): parameter names vary between runtimes.
        static bool InputBoxPrefix(object[] __args, ref string __result)
        {
            if (host == null || !host.OnUiThread) return true;
            string Prompt = __args.Length > 0 ? __args[0] as string : "";
            string Title = __args.Length > 1 ? __args[1] as string : "";
            string DefaultResponse = __args.Length > 2 ? __args[2] as string : "";
            using (var dlg = new BridgeDialog(string.IsNullOrEmpty(Title) ? "Input" : Title, Prompt, "question"))
            {
                var box = new TextBox { Text = DefaultResponse ?? "", Name = "response", Size = new Size(396, 20), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
                dlg.AddRow(box);
                dlg.AcceptButton = dlg.AddButton("OK", DialogResult.OK);
                dlg.CancelButton = dlg.AddButton("Cancel", DialogResult.Cancel);
                // VB returns an empty string for Cancel.
                __result = dlg.ShowDialog(host.ActiveForm()) == DialogResult.OK ? box.Text : "";
            }
            return false;
        }
    }
}
