using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MultiDeployOS.Master.Forms;
using MultiDeployOS.Master.Models;
using MultiDeployOS.Master.Services;

namespace MultiDeployOS.Master
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] App Main started\n");

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