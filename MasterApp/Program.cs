using System;
using System.Windows.Forms;
using MultiDeployOS.Master.Forms;

namespace MultiDeployOS.Master
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] App Main started\n");

            if (args.Length > 0 && args[0] == "--test-render")
            {
                ApplicationConfiguration.Initialize();
                var cfg = new MultiDeployOS.Master.Services.ConfigManager();
                
                using (var pForm = new PolicyEditorForm(cfg))
                {
                    pForm.Show();
                    Application.DoEvents();
                    var bmp = new System.Drawing.Bitmap(pForm.Width, pForm.Height);
                    pForm.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, pForm.Width, pForm.Height));
                    bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_policy_form.png"), System.Drawing.Imaging.ImageFormat.Png);
                    pForm.Close();
                }

                using (var rForm = new ImageRuleEditorForm(cfg))
                {
                    rForm.Show();
                    Application.DoEvents();
                    var bmp = new System.Drawing.Bitmap(rForm.Width, rForm.Height);
                    rForm.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, rForm.Width, rForm.Height));
                    bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_rule_form.png"), System.Drawing.Imaging.ImageFormat.Png);
                    rForm.Close();
                }
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Unhandled: {ex}\n");
            };
            Application.ThreadException += (s, e) =>
            {
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] ThreadException: {e.Exception}\n");
            };

            try
            {
                ApplicationConfiguration.Initialize();
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Initialized. Creating MainForm...\n");
                var form = new MainForm();
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] MainForm created. Running...\n");
                Application.Run(form);
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Application.Run returned\n");
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Catch: {ex}\n");
                MessageBox.Show("오류가 발생했습니다: " + ex.Message, "MultiDeployOS 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}