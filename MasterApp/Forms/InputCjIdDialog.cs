using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Forms
{
    public class InputCjIdDialog : Form
    {
        private readonly List<ClientSession> _targetSessions;
        private TextBox _txtCjId = null!;
        private CheckBox _chkProvisionerEnabled = null!;
        private CheckBox _chkAutoApproveNow = null!;
        private Label _lblTargetInfo = null!;

        public bool ShouldAutoApprove => _chkAutoApproveNow.Checked;

        public InputCjIdDialog(List<ClientSession> sessions)
        {
            _targetSessions = sessions ?? new List<ClientSession>();
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "🆔 CJ_ID (사번) 설정 및 무인 프로비저너 구성";
            this.Size = new Size(540, 390);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.White;
            this.Font = new Font("맑은 고딕", 9.5f);

            var pnlMain = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24)
            };

            var lblTitle = new Label
            {
                Text = "무인 사후 프로비저닝 (Post-OOBE Auto-Provisioner)",
                Font = new Font("맑은 고딕", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Location = new Point(24, 18)
            };

            string targetSummary = _targetSessions.Count == 1
                ? $"대상 PC: [{_targetSessions[0].IpAddress}] {_targetSessions[0].System.ModelName} ({_targetSessions[0].System.SerialNumber})"
                : $"선택된 {_targetSessions.Count}대의 대상 PC에 일괄 적용합니다.";

            _lblTargetInfo = new Label
            {
                Text = targetSummary,
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(24, 48)
            };

            var lblCjId = new Label
            {
                Text = "사용자 사번 (CJ_ID):",
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                AutoSize = true,
                Location = new Point(24, 84)
            };

            string initialCjId = _targetSessions.Count == 1 ? (_targetSessions[0].CjId ?? "") : "";
            _txtCjId = new TextBox
            {
                Location = new Point(24, 110),
                Size = new Size(476, 30),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 11.5f, FontStyle.Bold),
                Text = initialCjId
            };

            var lblHint = new Label
            {
                Text = "💡 사번 입력 시: setup_config.ini에 user_id={CJ_ID} 저장 후 AutoSetup2.0 무인 완주\n   공란 유지 시: user_id=\"\", sindoh_enabled=False 사전세팅 모드로 완주",
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("맑은 고딕", 8.5f),
                AutoSize = true,
                Location = new Point(24, 146)
            };

            _chkProvisionerEnabled = new CheckBox
            {
                Text = "무인 프로비저너 활성화 (Windows 첫 부팅 시 보안프로그램 자동 설치 및 무인 완주)",
                Checked = _targetSessions.All(s => s.IsProvisionerEnabled),
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 196)
            };

            bool anyReady = _targetSessions.Any(s => s.Status == "READY");
            _chkAutoApproveNow = new CheckBox
            {
                Text = "확인 시 즉시 배포 시작 (추가 승인 클릭 없이 자동으로 진행)",
                Checked = false, // 의도치 않은 자동 배포 방지 (기본값 OFF)
                Enabled = anyReady,
                ForeColor = Color.FromArgb(253, 224, 71),
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 228)
            };

            var pnlButtons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(20, 29, 48)
            };

            var btnOk = new Button
            {
                Text = "확인 (적용)",
                DialogResult = DialogResult.OK,
                Size = new Size(110, 36),
                Location = new Point(286, 12),
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => SaveChanges();

            var btnCancel = new Button
            {
                Text = "취소",
                DialogResult = DialogResult.Cancel,
                Size = new Size(100, 36),
                Location = new Point(406, 12),
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;

            pnlButtons.Controls.Add(btnOk);
            pnlButtons.Controls.Add(btnCancel);

            pnlMain.Controls.Add(lblTitle);
            pnlMain.Controls.Add(_lblTargetInfo);
            pnlMain.Controls.Add(lblCjId);
            pnlMain.Controls.Add(_txtCjId);
            pnlMain.Controls.Add(lblHint);
            pnlMain.Controls.Add(_chkProvisionerEnabled);
            pnlMain.Controls.Add(_chkAutoApproveNow);

            this.Controls.Add(pnlMain);
            this.Controls.Add(pnlButtons);
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void SaveChanges()
        {
            string enteredId = _txtCjId.Text.Trim();
            bool isEnabled = _chkProvisionerEnabled.Checked;

            foreach (var session in _targetSessions)
            {
                session.CjId = enteredId;
                session.IsProvisionerEnabled = isEnabled;
            }
        }
    }
}
