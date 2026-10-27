using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiDeployOS.Master.Models;
using MultiDeployOS.Master.Services;

namespace MultiDeployOS.Master.Forms
{
    public class SelectImageForm : Form
    {
        private readonly ConfigManager _configManager;
        private readonly List<ClientSession> _targetSessions;
        private readonly ClientSession _primarySession;

        // 라디오 버튼
        private RadioButton _rdoAuto;
        private RadioButton _rdoScanned;
        private RadioButton _rdoManual;

        // 컨트롤
        private Label _lblAutoPreview;
        private ComboBox _cboScannedWims;
        private Button _btnRefreshWims;
        private TextBox _txtManualPath;
        private Button _btnBrowseManual;
        private NumericUpDown _numWimIndex;
        private CheckBox _chkApplyToAll;

        private Button _btnOk;
        private Button _btnCancel;

        public SelectImageForm(ConfigManager configManager, List<ClientSession> sessions)
        {
            _configManager = configManager;
            _targetSessions = sessions ?? new List<ClientSession>();
            _primarySession = _targetSessions.FirstOrDefault() ?? new ClientSession();

            InitializeComponent();
            LoadScannedWims();
            PopulateCurrentState();
        }

        public SelectImageForm(ConfigManager configManager, ClientSession session)
            : this(configManager, new List<ClientSession> { session })
        {
        }

        private void InitializeComponent()
        {
            this.Text = "배포 이미지 선택 및 변경 (자동선택 디폴트 / 수동 별도지정)";
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(17, 24, 39);
            this.ForeColor = Color.White;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(680, 560);
            this.Padding = new Padding(20);

            var pnlMain = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            this.Controls.Add(pnlMain);

            // ====================================================================
            // 1. 대상 PC 정보 카드
            // ====================================================================
            var pnlPcInfo = new Panel
            {
                Dock = DockStyle.Top,
                Height = 85,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(14, 10, 14, 10),
                Margin = new Padding(0, 0, 0, 15)
            };

            string pcTitle = _targetSessions.Count > 1
                ? $"💻 대상 PC: {_targetSessions.Count}대 일괄 선택됨 (기준 PC: {_primarySession.IpAddress})"
                : $"💻 대상 PC: {_primarySession.IpAddress} ({_primarySession.System.Manufacturer} {_primarySession.System.ModelName})";

            var lblPcTitle = new Label
            {
                Text = pcTitle,
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            string autoRuleName = _primarySession.MatchedImage?.Name ?? "(매칭된 규칙 없음 - 기본 대체 규칙 적용)";
            string autoWimName = _primarySession.ResolvedImageFileName ?? _primarySession.MatchedImage?.TargetImage?.ImageFileName ?? "*.wim";

            var lblPcDetail = new Label
            {
                Text = $"매칭 규칙: {autoRuleName}\n디스크: {string.Join(", ", _primarySession.Disks.Select(d => $"[{d.DiskIndex}] {(int)d.SizeGb}GB {d.BusType}"))} | 시리얼: {_primarySession.System.SerialNumber}",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("맑은 고딕", 9f)
            };

            pnlPcInfo.Controls.Add(lblPcDetail);
            pnlPcInfo.Controls.Add(lblPcTitle);
            pnlMain.Controls.Add(pnlPcInfo);

            // ====================================================================
            // 2. 이미지 선택 그룹 (옵션 1: 자동, 옵션 2: 목록에서 별도선택, 옵션 3: 직접찾기)
            // ====================================================================
            var grpSelection = new GroupBox
            {
                Text = " 배포 WIM 이미지 선택 방식 ",
                Dock = DockStyle.Top,
                Height = 290,
                ForeColor = Color.FromArgb(147, 197, 253),
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Padding = new Padding(16, 20, 16, 14),
                BackColor = Color.FromArgb(15, 23, 42)
            };

            // Option 1: 기종/규칙 기반 자동 선택 (디폴트)
            _rdoAuto = new RadioButton
            {
                Text = "🎯 기종 및 하드웨어 규칙 기반 자동 선택 [디폴트 (기본값)]",
                Dock = DockStyle.Top,
                Height = 28,
                Checked = true,
                Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                Cursor = Cursors.Hand
            };
            _rdoAuto.CheckedChanged += (s, e) => UpdateOptionStates();

            _lblAutoPreview = new Label
            {
                Text = $"   👉 자동 감지 결과: {autoWimName} (규칙에 따른 최신 WIM 자동 할당)",
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("맑은 고딕", 9f)
            };

            // Option 2: 감지된 WIM 목록에서 별도 선택
            _rdoScanned = new RadioButton
            {
                Text = "📁 서버 이미지 저장 폴더에서 별도 선택 (수동 변경)",
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                Cursor = Cursors.Hand
            };
            _rdoScanned.CheckedChanged += (s, e) => UpdateOptionStates();

            var pnlScannedControls = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(24, 0, 0, 4)
            };

            _cboScannedWims = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("맑은 고딕", 9.5f),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };

            _btnRefreshWims = CreateButton("🔄 폴더 재스캔", Color.FromArgb(51, 65, 85));
            _btnRefreshWims.Dock = DockStyle.Right;
            _btnRefreshWims.Width = 110;
            _btnRefreshWims.Click += (s, e) => LoadScannedWims();

            pnlScannedControls.Controls.Add(_cboScannedWims);
            pnlScannedControls.Controls.Add(_btnRefreshWims);

            // Option 3: 직접 파일 찾아보기 또는 사용자 지정
            _rdoManual = new RadioButton
            {
                Text = "📂 직접 WIM 파일 경로 입력 또는 찾아보기",
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                Cursor = Cursors.Hand
            };
            _rdoManual.CheckedChanged += (s, e) => UpdateOptionStates();

            var pnlManualControls = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(24, 0, 0, 4)
            };

            _txtManualPath = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("맑은 고딕", 9.5f),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };

            _btnBrowseManual = CreateButton("찾아보기...", Color.FromArgb(71, 85, 105));
            _btnBrowseManual.Dock = DockStyle.Right;
            _btnBrowseManual.Width = 100;
            _btnBrowseManual.Click += BrowseManualFile;

            pnlManualControls.Controls.Add(_txtManualPath);
            pnlManualControls.Controls.Add(_btnBrowseManual);

            // Index 번호 설정 패널
            var pnlIndex = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(24, 6, 0, 0),
                FlowDirection = FlowDirection.LeftToRight
            };

            var lblIndex = new Label
            {
                Text = "WIM 이미지 Index 번호:",
                AutoSize = true,
                Padding = new Padding(0, 4, 10, 0),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("맑은 고딕", 9.5f)
            };

            _numWimIndex = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 99,
                Value = 1,
                Width = 75,
                Font = new Font("맑은 고딕", 9.5f),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };

            var lblIndexTip = new Label
            {
                Text = "(일반적으로 1번 Index를 사용합니다)",
                AutoSize = true,
                Padding = new Padding(8, 4, 0, 0),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("맑은 고딕", 8.5f)
            };

            pnlIndex.Controls.Add(lblIndex);
            pnlIndex.Controls.Add(_numWimIndex);
            pnlIndex.Controls.Add(lblIndexTip);

            // GroupBox에 추가 (DockStyle.Top 역순 고려 또는 Controls 순서 주의)
            grpSelection.Controls.Add(pnlIndex);
            grpSelection.Controls.Add(pnlManualControls);
            grpSelection.Controls.Add(_rdoManual);
            grpSelection.Controls.Add(pnlScannedControls);
            grpSelection.Controls.Add(_rdoScanned);
            grpSelection.Controls.Add(_lblAutoPreview);
            grpSelection.Controls.Add(_rdoAuto);

            pnlMain.Controls.Add(grpSelection);

            // ====================================================================
            // 3. 하단 일괄 적용 옵션 및 버튼 바
            // ====================================================================
            var pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 65,
                BackColor = Color.Transparent
            };

            _chkApplyToAll = new CheckBox
            {
                Text = $"현재 연결된 모든 PC(총 {_targetSessions.Count}대)에 이 이미지 설정 일괄 적용",
                AutoSize = true,
                Dock = DockStyle.Top,
                ForeColor = Color.FromArgb(253, 224, 71),
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Visible = _targetSessions.Count > 1,
                Checked = _targetSessions.Count > 1
            };

            var pnlButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 4, 0, 0)
            };

            _btnCancel = CreateButton("취소", Color.FromArgb(71, 85, 105));
            _btnCancel.Size = new Size(100, 36);
            _btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            _btnOk = CreateButton("✔ 이미지 설정 적용", Color.FromArgb(14, 165, 233));
            _btnOk.Size = new Size(160, 36);
            _btnOk.Font = new Font("맑은 고딕", 10f, FontStyle.Bold);
            _btnOk.Click += ApplySelectionAndClose;

            pnlButtons.Controls.Add(_btnCancel);
            pnlButtons.Controls.Add(_btnOk);

            pnlBottom.Controls.Add(_chkApplyToAll);
            pnlBottom.Controls.Add(pnlButtons);
            pnlMain.Controls.Add(pnlBottom);
        }

        private void UpdateOptionStates()
        {
            _cboScannedWims.Enabled = _rdoScanned.Checked;
            _btnRefreshWims.Enabled = _rdoScanned.Checked;

            _txtManualPath.Enabled = _rdoManual.Checked;
            _btnBrowseManual.Enabled = _rdoManual.Checked;

            if (_rdoAuto.Checked)
            {
                _rdoAuto.ForeColor = Color.FromArgb(52, 211, 153);
                _rdoScanned.ForeColor = Color.FromArgb(203, 213, 225);
                _rdoManual.ForeColor = Color.FromArgb(203, 213, 225);
            }
            else if (_rdoScanned.Checked)
            {
                _rdoAuto.ForeColor = Color.FromArgb(203, 213, 225);
                _rdoScanned.ForeColor = Color.FromArgb(56, 189, 248);
                _rdoManual.ForeColor = Color.FromArgb(203, 213, 225);
            }
            else
            {
                _rdoAuto.ForeColor = Color.FromArgb(203, 213, 225);
                _rdoScanned.ForeColor = Color.FromArgb(203, 213, 225);
                _rdoManual.ForeColor = Color.FromArgb(56, 189, 248);
            }
        }

        private void LoadScannedWims()
        {
            _cboScannedWims.Items.Clear();
            string masterDir = _configManager.ServerConfig.MasterImageDir ?? @"C:\images";
            var scan = WimScanner.ScanFolder(masterDir);

            if (scan.Exists && scan.Files.Count > 0)
            {
                foreach (var f in scan.Files)
                {
                    _cboScannedWims.Items.Add(new WimlItem(f.FileName, $"{f.FileName} ({f.SizeGb} GB, 수정: {f.ModifiedAt})"));
                }
                if (_cboScannedWims.Items.Count > 0) _cboScannedWims.SelectedIndex = 0;
            }
            else
            {
                _cboScannedWims.Items.Add(new WimlItem("", "-- 감지된 WIM 파일 없음 (폴더 설정 확인 필요) --"));
                _cboScannedWims.SelectedIndex = 0;
            }
        }

        private void PopulateCurrentState()
        {
            if (_primarySession.IsCustomImageSelected && !string.IsNullOrWhiteSpace(_primarySession.CustomImageFileName))
            {
                string custom = _primarySession.CustomImageFileName;
                _numWimIndex.Value = _primarySession.CustomImageIndex ?? 1;

                // 감지된 목록에 있는지 확인
                int matchedIdx = -1;
                for (int i = 0; i < _cboScannedWims.Items.Count; i++)
                {
                    if (_cboScannedWims.Items[i] is WimlItem item && item.FileName.Equals(custom, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedIdx = i;
                        break;
                    }
                }

                if (matchedIdx >= 0)
                {
                    _rdoScanned.Checked = true;
                    _cboScannedWims.SelectedIndex = matchedIdx;
                }
                else
                {
                    _rdoManual.Checked = true;
                    _txtManualPath.Text = custom;
                }
            }
            else
            {
                // 기본값: 자동 선택
                _rdoAuto.Checked = true;
                _numWimIndex.Value = _primarySession.MatchedImage?.TargetImage?.ImageIndex ?? 1;
            }

            UpdateOptionStates();
        }

        private void BrowseManualFile(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "배포할 WIM / ESD 이미지 파일 선택",
                Filter = "Windows 이미지 파일 (*.wim;*.esd;*.swm)|*.wim;*.esd;*.swm|모든 파일 (*.*)|*.*",
                InitialDirectory = _configManager.ServerConfig.MasterImageDir ?? @"C:\images"
            };

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _txtManualPath.Text = ofd.FileName;
            }
        }

        private void ApplySelectionAndClose(object? sender, EventArgs e)
        {
            var targets = _chkApplyToAll.Visible && _chkApplyToAll.Checked
                ? _targetSessions
                : new List<ClientSession> { _primarySession };

            int targetIndex = (int)_numWimIndex.Value;

            if (_rdoAuto.Checked)
            {
                // 디폴트 자동 선택으로 복귀
                foreach (var s in targets)
                {
                    s.ResetToAutoImage();
                }
            }
            else if (_rdoScanned.Checked)
            {
                if (_cboScannedWims.SelectedItem is WimlItem selected && !string.IsNullOrEmpty(selected.FileName))
                {
                    foreach (var s in targets)
                    {
                        s.SetCustomImage(selected.FileName, targetIndex);
                    }
                }
                else
                {
                    MessageBox.Show("목록에서 유효한 WIM 파일을 선택하세요.", "선택 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else // _rdoManual.Checked
            {
                string custom = _txtManualPath.Text.Trim();
                if (string.IsNullOrEmpty(custom))
                {
                    MessageBox.Show("적용할 WIM 파일명 또는 파일 경로를 입력하세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string fileName = Path.GetFileName(custom);
                foreach (var s in targets)
                {
                    s.SetCustomImage(fileName, targetIndex);
                }
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private Button CreateButton(string text, Color backColor)
        {
            return new Button
            {
                Text = text,
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                Cursor = Cursors.Hand,
                Height = 32,
                Margin = new Padding(0, 0, 6, 0)
            };
        }

        private class WimlItem
        {
            public string FileName { get; }
            public string DisplayText { get; }

            public WimlItem(string fileName, string displayText)
            {
                FileName = fileName;
                DisplayText = displayText;
            }

            public override string ToString() => DisplayText;
        }
    }
}
