// xulj-host.exe: serves an existing, unmodified WinForms application to browsers.
//   mono xulj-host.exe App.exe [--form Namespace.MainForm] [--port 8092] [--host *]
// The app's own Main() is not run: the bridge instantiates its form once per browser session.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace XulJ.Bridge
{
    static class Launcher
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: xulj-host App.exe [--form Type.Name] [--port 8092] [--host *]");
                return 2;
            }
            string appPath = Path.GetFullPath(args[0]);
            string formName = Arg(args, "--form");
            var options = new BridgeOptions();
            if (Arg(args, "--port") != null) options.Port = int.Parse(Arg(args, "--port"));
            if (Arg(args, "--host") != null) options.Host = Arg(args, "--host");

            // Resolve the app's own dependencies from its directory.
            string appDir = Path.GetDirectoryName(appPath);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string candidate = Path.Combine(appDir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            Directory.SetCurrentDirectory(appDir);

            var asm = Assembly.LoadFrom(appPath);
            var forms = asm.GetTypes()
                .Where(t => typeof(Form).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();
            Type formType = formName != null
                ? forms.FirstOrDefault(t => t.FullName == formName || t.Name == formName)
                : forms.FirstOrDefault(t => t.Name == "MainForm") ?? forms.FirstOrDefault(t => t.Name == "Form1") ?? (forms.Count == 1 ? forms[0] : null);
            if (formType == null)
            {
                Console.Error.WriteLine("Pick a form with --form. Candidates: " + string.Join(", ", forms.Select(t => t.FullName)));
                return 2;
            }

            Application.EnableVisualStyles();
            Console.WriteLine("hosting " + formType.FullName + " from " + Path.GetFileName(appPath));
            XulJBridge.Run(() => (Form)Activator.CreateInstance(formType), options);
            return 0;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
