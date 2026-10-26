using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiDeployOS.Master.Models;
using MultiDeployOS.Master.Services;

namespace MultiDeployOS.Master.Forms
{
    public class ImageRuleEditorForm : Form
    {
        private readonly ConfigManager _configManager;
        private TextBox _txtMasterDir;
        private TextBox _txtSharePath;
        private Label _lblScanResult;
        private ListBox _listScannedWims;

        private ListView _lvRules;
        private TextBox _txtRuleName;
        private ComboBox _cboBus;
        private TextBox _txtMfg;
        private TextBox _txtModel;
        private NumericUpDown _numMinDisk;
        private NumericUpDown _numMaxDisk;
        private CheckBox _chkEnabled;
        private ComboBox _cboWimHelper;
        private TextBox _txtWimName;
        private NumericUpDown _numWimIndex;
        private TextBox _txtRuleDesc;

        private Button _btnNewRule;
        private Button _btnDeleteRule;
        private Button _btnSaveRule;
        private Button _btnPriorityUp;
        private Button _btnPriorityDown;

        private ImageRule? _currentSelectedRule;

        public ImageRuleEditorForm(ConfigManager configManager)
        {
            _configManager = configManager;
            InitializeComponent();
            LoadFolderSettings();
            RefreshScannedFiles();
            LoadRulesList();
        }

        private void InitializeComponent()
        {
            this.Text = "마스터 이미지 폴더 및 선택 규칙 관리";
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(24, 28, 36);
            this.ForeColor = Color.White;
            this.AutoScaleMode = AutoScaleMode.Dpi;

            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            int targetW = Math.Min(1180, Math.Max(880, workArea.Width - 40));
            int targetH = Math.Min(760, Math.Max(540, workArea.Height - 50));
            this.Size = new Size(targetW, targetH);
            this.MinimumSize = new Size(880, 520);
            this.StartPosition = FormStartPosition.CenterParent;

            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 6)
            };
            this.Controls.Add(tabControl);

            // 탭 1: 이미지 선택 규칙
            var tabRules = new TabPage("🎯 이미지 자동 선택 규칙");
            tabRules.BackColor = Color.FromArgb(24, 28, 36);
            InitRulesTab(tabRules);
            tabControl.TabPages.Add(tabRules);

            // 탭 2: 마스터 이미지 저장 폴더 설정
            var tabFolder = new TabPage("📁 마스터 이미지 저장 폴더 설정");
            tabFolder.BackColor = Color.FromArgb(24, 28, 36);
            InitFolderTab(tabFolder);
            tabControl.TabPages.Add(tabFolder);
        }

        private void InitFolderTab(TabPage page)
        {
            var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };

            var lblTitle = new Label { Text = "마스터 이미지 파일 보관 폴더", Dock = DockStyle.Top, Height = 30, Font = new Font("맑은 고딕", 11f, FontStyle.Bold), ForeColor = Color.FromArgb(56, 189, 248) };
            pnl.Controls.Add(lblTitle);

            var formTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 10, 0, 10) };
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

            // 로컬 폴더
            var lblLocal = new Label { Text = "로컬 이미지 폴더 경로:", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(203, 213, 225) };
            _txtMasterDir = new TextBox { Dock = DockStyle.Fill };
            var btnBrowse = CreateButton("찾아보기...", Color.FromArgb(71, 85, 105));
            btnBrowse.Click += (s, e) =>
            {
                using var fbd = new FolderBrowserDialog { SelectedPath = _txtMasterDir.Text };
                if (fbd.ShowDialog() == DialogResult.OK) _txtMasterDir.Text = fbd.SelectedPath;
            };
            formTable.Controls.Add(lblLocal, 0, 0);
            formTable.Controls.Add(_txtMasterDir, 1, 0);
            formTable.Controls.Add(btnBrowse, 2, 0);

            // 네트워크 공유 경로
            var lblShare = new Label { Text = "WinPE 클라이언트 공유 경로:", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(203, 213, 225) };
            _txtSharePath = new TextBox { Dock = DockStyle.Fill };
            formTable.Controls.Add(lblShare, 0, 1);
            formTable.Controls.Add(_txtSharePath, 1, 1);

            pnl.Controls.Add(formTable);

            var pnlBtn = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(0, 8, 0, 0) };
            var btnSaveScan = CreateButton("💾 폴더 경로 저장 & WIM 파일 스캔", Color.FromArgb(14, 165, 233));
            btnSaveScan.Height = 36;
            btnSaveScan.Font = new Font("맑은 고딕", 10f, FontStyle.Bold);
            btnSaveScan.Click += (s, e) =>
            {
                string mDir = (_txtMasterDir.Text ?? "").Trim().Trim('"', '\'').Replace('/', '\\');
                string sPath = (_txtSharePath.Text ?? "").Trim().Trim('"', '\'').Replace('/', '\\');

                if (mDir.Length > 3) mDir = mDir.TrimEnd('\\');
                if (sPath.Length > 2 && !sPath.EndsWith(":")) sPath = sPath.TrimEnd('\\');

                _txtMasterDir.Text = mDir;
                _txtSharePath.Text = sPath;

                _configManager.ServerConfig.MasterImageDir = mDir;
                _configManager.ServerConfig.NetworkSharePath = sPath;
                _configManager.SaveServerConfig();
                RefreshScannedFiles();
                MessageBox.Show("폴더 설정이 저장되고 WIM 파일 스캔이 완료되었습니다.", "스캔 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            pnlBtn.Controls.Add(btnSaveScan);
            pnl.Controls.Add(pnlBtn);

            _lblScanResult = new Label { Dock = DockStyle.Top, Height = 30, ForeColor = Color.FromArgb(52, 211, 153), Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };
            pnl.Controls.Add(_lblScanResult);

            _listScannedWims = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 20, 28),
                ForeColor = Color.FromArgb(125, 211, 252),
                ItemHeight = 26,
                Font = new Font("Consolas", 9.5f)
            };
            pnl.Controls.Add(_listScannedWims);

            page.Controls.Add(pnl);
        }

        private void InitRulesTab(TabPage page)
        {
            // 좌측: 규칙 목록 리스트뷰 패널 (너비 460px 확보)
            var pnlLeft = new Panel
            {
                Dock = DockStyle.Left,
                Width = 460,
                BackColor = Color.FromArgb(20, 24, 32),
                Padding = new Padding(12)
            };

            var lblRulesTitle = new Label
            {
                Text = "등록된 이미지 선택 규칙 (우선순위 순)",
                Dock = DockStyle.Top,
                Height = 32,
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            _lvRules = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(15, 20, 28),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 9.5f)
            };
            _lvRules.Columns.Add("우선순위", 75);
            _lvRules.Columns.Add("규칙 이름", 175);
            _lvRules.Columns.Add("할당 WIM", 130);
            _lvRules.Columns.Add("상태", 55);
            _lvRules.SelectedIndexChanged += LvRules_SelectedIndexChanged;

            // 버튼 영역 (2행 2열 TableLayoutPanel 배치로 버튼 잘림 방지)
            var pnlRuleBtns = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 84,
                RowCount = 2,
                ColumnCount = 2,
                Padding = new Padding(0, 6, 0, 0)
            };
            pnlRuleBtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pnlRuleBtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pnlRuleBtns.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            pnlRuleBtns.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            _btnPriorityUp = CreateButton("▲ 우선순위 올림", Color.FromArgb(51, 65, 85));
            _btnPriorityUp.AutoSize = false;
            _btnPriorityUp.Dock = DockStyle.Fill;
            _btnPriorityUp.Margin = new Padding(3);
            _btnPriorityUp.Click += (s, e) => MovePriority(-1);

            _btnPriorityDown = CreateButton("▼ 우선순위 내림", Color.FromArgb(51, 65, 85));
            _btnPriorityDown.AutoSize = false;
            _btnPriorityDown.Dock = DockStyle.Fill;
            _btnPriorityDown.Margin = new Padding(3);
            _btnPriorityDown.Click += (s, e) => MovePriority(1);

            _btnNewRule = CreateButton("+ 새 규칙 추가", Color.FromArgb(16, 185, 129));
            _btnNewRule.AutoSize = false;
            _btnNewRule.Dock = DockStyle.Fill;
            _btnNewRule.Margin = new Padding(3);
            _btnNewRule.Click += (s, e) => CreateNewRule();

            _btnDeleteRule = CreateButton("🗑️ 규칙 삭제", Color.FromArgb(239, 68, 68));
            _btnDeleteRule.AutoSize = false;
            _btnDeleteRule.Dock = DockStyle.Fill;
            _btnDeleteRule.Margin = new Padding(3);
            _btnDeleteRule.Click += (s, e) => DeleteCurrentRule();

            pnlRuleBtns.Controls.Add(_btnPriorityUp, 0, 0);
            pnlRuleBtns.Controls.Add(_btnPriorityDown, 1, 0);
            pnlRuleBtns.Controls.Add(_btnNewRule, 0, 1);
            pnlRuleBtns.Controls.Add(_btnDeleteRule, 1, 1);

            // 좌측 패널 컨트롤 추가 (ListView Fill, Title Top, Buttons Bottom)
            pnlLeft.Controls.Add(_lvRules);
            pnlLeft.Controls.Add(lblRulesTitle);
            pnlLeft.Controls.Add(pnlRuleBtns);

            // 좌우 분할 스플리터
            var spl = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 6,
                BackColor = Color.FromArgb(40, 48, 62)
            };

            // 우측: 규칙 편집 패널 (남은 가로 전체 차지)
            var pnlRight = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 28, 36),
                Padding = new Padding(24, 16, 24, 16),
                AutoScroll = true
            };

            var lblEditTitle = new Label
            {
                Text = "선택 규칙 상세 편집",
                Dock = DockStyle.Top,
                Height = 36,
                Font = new Font("맑은 고딕", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            var formTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(0, 6, 0, 10)
            };
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddFormRow(formTable, "규칙 이름:", _txtRuleName = new TextBox { Width = 420 });

            _cboBus = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            _cboBus.Items.AddRange(new object[] { "ANY (모든 버스)", "NVMe", "SATA" });
            _cboBus.SelectedIndex = 0;
            AddFormRow(formTable, "디스크 인터페이스:", _cboBus);

            AddFormRow(formTable, "제조사 패턴:", _txtMfg = new TextBox { Width = 420, Text = "*" });
            AddFormRow(formTable, "모델명 패턴:", _txtModel = new TextBox { Width = 420, Text = "*" });

            var pnlDiskCap = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _numMinDisk = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = 100, Width = 90 };
            _numMaxDisk = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = 4000, Width = 90 };
            pnlDiskCap.Controls.Add(_numMinDisk);
            pnlDiskCap.Controls.Add(new Label { Text = "GB ~ ", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            pnlDiskCap.Controls.Add(_numMaxDisk);
            pnlDiskCap.Controls.Add(new Label { Text = "GB", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            AddFormRow(formTable, "디스크 용량 범위:", pnlDiskCap);

            _chkEnabled = new CheckBox { Text = "이 선택 규칙 활성화 (Enabled)", AutoSize = true, Checked = true, ForeColor = Color.FromArgb(52, 211, 153) };
            AddFormRow(formTable, "활성 상태:", _chkEnabled);

            // WIM 파일 지정
            var pnlWimSelect = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            _cboWimHelper = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
            _cboWimHelper.SelectedIndexChanged += (s, e) =>
            {
                if (_cboWimHelper.SelectedIndex > 0)
                {
                    _txtWimName.Text = _cboWimHelper.SelectedItem?.ToString() ?? "";
                }
            };

            _txtWimName = new TextBox { Width = 460 };
            pnlWimSelect.Controls.Add(new Label { Text = "감지된 WIM에서 빠른 선택:", AutoSize = true, ForeColor = Color.FromArgb(148, 163, 184) });
            pnlWimSelect.Controls.Add(_cboWimHelper);
            pnlWimSelect.Controls.Add(new Label { Text = "적용 WIM 파일명:", AutoSize = true, ForeColor = Color.FromArgb(203, 213, 225), Margin = new Padding(0, 4, 0, 0) });
            pnlWimSelect.Controls.Add(_txtWimName);
            AddFormRow(formTable, "할당 WIM 파일:", pnlWimSelect);

            _numWimIndex = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 1, Width = 90 };
            AddFormRow(formTable, "WIM 이미지 Index:", _numWimIndex);

            _txtRuleDesc = new TextBox { Width = 460, Text = "기종별 맞춤형 Windows 이미지 배포" };
            AddFormRow(formTable, "규칙 설명:", _txtRuleDesc);

            // 저장 버튼 패널
            var pnlSave = new Panel { Dock = DockStyle.Top, Height = 65, Padding = new Padding(0, 16, 0, 0) };
            _btnSaveRule = CreateButton("💾 규칙 저장하기", Color.FromArgb(14, 165, 233));
            _btnSaveRule.Size = new Size(220, 40);
            _btnSaveRule.Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold);
            _btnSaveRule.Click += (s, e) => SaveCurrentRule();
            pnlSave.Controls.Add(_btnSaveRule);

            // ★ 중요: DockStyle.Top 컨트롤들은 역순으로 추가해야 위에서부터 올바르게 배치됨
            pnlRight.Controls.Add(pnlSave);
            pnlRight.Controls.Add(formTable);
            pnlRight.Controls.Add(lblEditTitle);

            // TabPage에 도킹 추가
            page.Controls.Add(pnlRight);
            page.Controls.Add(spl);
            page.Controls.Add(pnlLeft);
            pnlRight.BringToFront();
        }

        private void AddFormRow(TableLayoutPanel table, string labelText, Control control)
        {
            var lbl = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(203, 213, 225) };
            table.Controls.Add(lbl);
            table.Controls.Add(control);
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
                AutoSize = true,
                Height = 32,
                Margin = new Padding(0, 0, 8, 0)
            };
        }

        private void LoadFolderSettings()
        {
            _txtMasterDir.Text = _configManager.ServerConfig.MasterImageDir;
            _txtSharePath.Text = _configManager.ServerConfig.NetworkSharePath;
        }

        private void RefreshScannedFiles()
        {
            var scan = WimScanner.ScanFolder(_configManager.ServerConfig.MasterImageDir);
            _listScannedWims.Items.Clear();
            _cboWimHelper.Items.Clear();
            _cboWimHelper.Items.Add("-- 감지된 WIM 파일 목록 --");

            if (scan.Exists && scan.Files.Count > 0)
            {
                _lblScanResult.Text = $"✅ 총 {scan.TotalFiles}개의 WIM/ESD 이미지 감지됨 ({scan.TotalSizeGb} GB)";
                _lblScanResult.ForeColor = Color.FromArgb(52, 211, 153);

                foreach (var f in scan.Files)
                {
                    _listScannedWims.Items.Add($"📀 {f.FileName,-35} | {f.SizeGb} GB | {f.ModifiedAt}");
                    _cboWimHelper.Items.Add(f.FileName);
                }
            }
            else
            {
                _lblScanResult.Text = scan.Error ?? "⚠️ 지정된 폴더에 .wim 파일이 없습니다.";
                _lblScanResult.ForeColor = Color.FromArgb(248, 113, 113);
            }
            _cboWimHelper.SelectedIndex = 0;
        }

        private void LoadRulesList()
        {
            _lvRules.Items.Clear();
            var sorted = _configManager.ImageCatalog.OrderBy(r => r.IsFallback ? 9999 : r.Priority).ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var r = sorted[i];
                string priorityText = r.IsFallback ? "대체" : $"#{r.Priority}";
                string wimName = r.TargetImage?.ImageFileName ?? "미지정.wim";
                string statusText = r.Enabled ? "활성" : "비활성";

                var item = new ListViewItem(new[] { priorityText, r.Name, wimName, statusText })
                {
                    Tag = r
                };
                if (!r.Enabled) item.ForeColor = Color.FromArgb(148, 163, 184);
                _lvRules.Items.Add(item);
            }

            if (_lvRules.Items.Count > 0)
            {
                _lvRules.Items[0].Selected = true;
            }
        }

        private void LvRules_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_lvRules.SelectedItems.Count > 0)
            {
                _currentSelectedRule = _lvRules.SelectedItems[0].Tag as ImageRule;
                if (_currentSelectedRule != null)
                {
                    BindRuleToForm(_currentSelectedRule);
                }
            }
        }

        private void BindRuleToForm(ImageRule r)
        {
            _txtRuleName.Text = r.Name;
            string busType = r.Conditions?.TargetDiskType ?? "ANY";
            if (busType.StartsWith("ANY", StringComparison.OrdinalIgnoreCase)) _cboBus.SelectedIndex = 0;
            else if (busType.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) _cboBus.SelectedIndex = 1;
            else if (busType.Equals("SATA", StringComparison.OrdinalIgnoreCase)) _cboBus.SelectedIndex = 2;
            else _cboBus.SelectedIndex = 0;

            _txtMfg.Text = r.Conditions?.Manufacturer ?? "*";
            _txtModel.Text = r.Conditions?.ModelPattern ?? "*";
            _numMinDisk.Value = r.Conditions?.MinDiskGb ?? 0;
            _numMaxDisk.Value = r.Conditions?.MaxDiskGb ?? 99999;
            _chkEnabled.Checked = r.Enabled;

            _txtWimName.Text = r.TargetImage?.ImageFileName ?? "Win11_Custom.wim";
            _numWimIndex.Value = r.TargetImage?.ImageIndex ?? 1;
            _txtRuleDesc.Text = r.TargetImage?.Description ?? "";
        }

        private void CreateNewRule()
        {
            string firstWim = _cboWimHelper.Items.Count > 1 ? _cboWimHelper.Items[1].ToString()! : "Win11_Custom.wim";
            _currentSelectedRule = new ImageRule
            {
                Id = "rule-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Name = "신규 기종 이미지 규칙",
                Priority = _configManager.ImageCatalog.Count(r => !r.IsFallback) + 1,
                Enabled = true,
                Conditions = new ImageRuleConditions { Manufacturer = "*", ModelPattern = "*" },
                TargetImage = new TargetImageInfo { ImageFileName = firstWim, ImageIndex = 1, Description = "기종별 맞춤형 Windows 이미지 배포" }
            };
            BindRuleToForm(_currentSelectedRule);
            _txtRuleName.Focus();
        }

        private void SaveCurrentRule()
        {
            string name = _txtRuleName.Text.Trim();
            string wim = _txtWimName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("규칙 이름을 입력하세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(wim))
            {
                MessageBox.Show("할당할 WIM 파일명을 입력하세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_currentSelectedRule == null)
            {
                _currentSelectedRule = new ImageRule { Id = "rule-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            }

            _currentSelectedRule.Name = name;
            _currentSelectedRule.Enabled = _chkEnabled.Checked;
            _currentSelectedRule.Conditions.Manufacturer = _txtMfg.Text.Trim();
            _currentSelectedRule.Conditions.ModelPattern = _txtModel.Text.Trim();
            _currentSelectedRule.Conditions.MinDiskGb = (int)_numMinDisk.Value;
            _currentSelectedRule.Conditions.MaxDiskGb = (int)_numMaxDisk.Value;

            string selectedBus = _cboBus.SelectedItem?.ToString() ?? "ANY";
            if (selectedBus.StartsWith("ANY", StringComparison.OrdinalIgnoreCase)) selectedBus = "ANY";
            _currentSelectedRule.Conditions.TargetDiskType = selectedBus;

            _currentSelectedRule.TargetImage.ImageFileName = wim;
            _currentSelectedRule.TargetImage.ImageIndex = (int)_numWimIndex.Value;
            _currentSelectedRule.TargetImage.Description = _txtRuleDesc.Text.Trim();

            if (!_configManager.ImageCatalog.Contains(_currentSelectedRule))
            {
                // Fallback 규칙 바로 앞에 삽입
                int fallbackIdx = _configManager.ImageCatalog.FindIndex(r => r.IsFallback);
                if (fallbackIdx >= 0)
                {
                    _configManager.ImageCatalog.Insert(fallbackIdx, _currentSelectedRule);
                }
                else
                {
                    _configManager.ImageCatalog.Add(_currentSelectedRule);
                }
            }

            ReassignPriorities();
            _configManager.SaveImageCatalog();
            LoadRulesList();
            MessageBox.Show($"규칙 '{name}'이(가) 저장되었습니다.", "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeleteCurrentRule()
        {
            if (_currentSelectedRule == null) return;
            if (_currentSelectedRule.IsFallback)
            {
                MessageBox.Show("기본 대체(Fallback) 규칙은 모든 기종 미매칭 시 사용되는 기본 안전망이므로 삭제할 수 없습니다.", "삭제 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show($"정말로 '{_currentSelectedRule.Name}' 규칙을 삭제하시겠습니까?", "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _configManager.ImageCatalog.Remove(_currentSelectedRule);
                ReassignPriorities();
                _configManager.SaveImageCatalog();
                LoadRulesList();
            }
        }

        private void MovePriority(int direction)
        {
            if (_currentSelectedRule == null || _currentSelectedRule.IsFallback) return;

            var nonFallback = _configManager.ImageCatalog.Where(r => !r.IsFallback).OrderBy(r => r.Priority).ToList();
            int idx = nonFallback.IndexOf(_currentSelectedRule);
            if (idx < 0) return;

            int targetIdx = idx + direction;
            if (targetIdx < 0 || targetIdx >= nonFallback.Count) return;

            var temp = nonFallback[idx];
            nonFallback[idx] = nonFallback[targetIdx];
            nonFallback[targetIdx] = temp;

            for (int i = 0; i < nonFallback.Count; i++)
            {
                nonFallback[i].Priority = i + 1;
            }

            _configManager.SaveImageCatalog();
            LoadRulesList();
        }

        private void ReassignPriorities()
        {
            var nonFallback = _configManager.ImageCatalog.Where(r => !r.IsFallback).OrderBy(r => r.Priority).ToList();
            for (int i = 0; i < nonFallback.Count; i++)
            {
                nonFallback[i].Priority = i + 1;
            }
        }
    }
}
