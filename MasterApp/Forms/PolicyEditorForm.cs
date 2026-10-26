using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiDeployOS.Master.Models;
using MultiDeployOS.Master.Services;

namespace MultiDeployOS.Master.Forms
{
    public class PolicyEditorForm : Form
    {
        private readonly ConfigManager _configManager;
        private ListBox _listPolicies;
        private TextBox _txtName;
        private ComboBox _cboScheme;
        private TextBox _txtDesc;
        private TextBox _txtModel;
        private ComboBox _cboDiskCriteria;
        private NumericUpDown _numMinCap;
        private NumericUpDown _numMaxCap;
        private CheckBox _chkAutoApprove;
        private TextBox _txtCommands;

        private Button _btnNew;
        private Button _btnDelete;
        private Button _btnSave;
        private PartitionPolicy? _currentSelected;

        public PolicyEditorForm(ConfigManager configManager)
        {
            _configManager = configManager;
            InitializeComponent();
            LoadPolicyList();
        }

        private void InitializeComponent()
        {
            this.Text = "파티션 권장양식 관리";
            this.Size = new Size(1180, 780);
            this.MinimumSize = new Size(950, 650);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(24, 28, 36);
            this.ForeColor = Color.White;

            // 좌측 목록 패널 (고정 너비 330px)
            var pnlLeft = new Panel
            {
                Dock = DockStyle.Left,
                Width = 330,
                BackColor = Color.FromArgb(20, 24, 32),
                Padding = new Padding(12)
            };

            var lblListTitle = new Label
            {
                Text = "등록된 권장양식 목록",
                Dock = DockStyle.Top,
                Height = 32,
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            _listPolicies = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 20, 28),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ItemHeight = 26,
                Font = new Font("맑은 고딕", 9.5f)
            };
            _listPolicies.SelectedIndexChanged += ListPolicies_SelectedIndexChanged;

            var pnlListButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 8, 0, 0)
            };
            _btnNew = CreateButton("+ 새 양식 추가", Color.FromArgb(16, 185, 129));
            _btnNew.Click += (s, e) => CreateNewPolicy();
            _btnDelete = CreateButton("양식 삭제", Color.FromArgb(239, 68, 68));
            _btnDelete.Click += (s, e) => DeleteCurrentPolicy();

            pnlListButtons.Controls.Add(_btnNew);
            pnlListButtons.Controls.Add(_btnDelete);

            pnlLeft.Controls.Add(_listPolicies);
            pnlLeft.Controls.Add(lblListTitle);
            pnlLeft.Controls.Add(pnlListButtons);

            // 좌우 스플리터
            var spl = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 6,
                BackColor = Color.FromArgb(40, 48, 62)
            };

            // 우측 편집 패널 (남은 가로 전체 800px+ 차지)
            var pnlRight = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 28, 36),
                Padding = new Padding(24, 16, 24, 16),
                AutoScroll = true
            };

            var lblEditTitle = new Label
            {
                Text = "권장양식 상세 설정",
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
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddFormRow(formTable, "양식 이름:", _txtName = new TextBox { Width = 450, Font = new Font("맑은 고딕", 9.5f) });
            AddFormRow(formTable, "파티션 형식:", _cboScheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 });
            _cboScheme.Items.AddRange(new object[] { "GPT", "MBR" });
            _cboScheme.SelectedIndex = 0;

            AddFormRow(formTable, "설명 (메모):", _txtDesc = new TextBox { Width = 500, Font = new Font("맑은 고딕", 9.5f) });
            AddFormRow(formTable, "모델 매칭 패턴:", _txtModel = new TextBox { Width = 400, Text = "*", Font = new Font("맑은 고딕", 9.5f) });

            AddFormRow(formTable, "타깃 디스크:", _cboDiskCriteria = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 });
            _cboDiskCriteria.Items.AddRange(new object[] { "SMART_AUTO (지능형 자동)", "NVME_FIRST (NVMe 우선)", "INDEX_0 (디스크 0번)" });
            _cboDiskCriteria.SelectedIndex = 0;

            var pnlCap = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _numMinCap = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = 0, Width = 90 };
            _numMaxCap = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = 99999, Width = 90 };
            pnlCap.Controls.Add(_numMinCap);
            pnlCap.Controls.Add(new Label { Text = "GB ~ ", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            pnlCap.Controls.Add(_numMaxCap);
            pnlCap.Controls.Add(new Label { Text = "GB", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            AddFormRow(formTable, "디스크 용량 범위:", pnlCap);

            _chkAutoApprove = new CheckBox
            {
                Text = "조건 일치 시 자동 배포 승인 (Auto-Approve)",
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(52, 211, 153),
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold)
            };
            AddFormRow(formTable, "자동 승인:", _chkAutoApprove);

            // 템플릿 버튼 바 (2x2 그리드 배열로 버튼 잘림 없이 깔끔하게 배치)
            var grpTemplate = new GroupBox
            {
                Text = "💡 조건별 추천 템플릿 빠른 적용 (클릭 시 자동 세팅)",
                Dock = DockStyle.Top,
                Height = 98,
                ForeColor = Color.FromArgb(148, 163, 184),
                Padding = new Padding(10, 8, 10, 8)
            };
            var tblTemplate = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2
            };
            tblTemplate.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tblTemplate.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tblTemplate.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            tblTemplate.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var btnTplSingleC = CreateButton("단일 C: 전체 할당 (기본)", Color.FromArgb(71, 85, 105));
            btnTplSingleC.Dock = DockStyle.Fill;
            btnTplSingleC.Margin = new Padding(3);
            btnTplSingleC.Click += (s, e) => ApplyTemplateSingleC();

            var btnTplOver512 = CreateButton("[512GB 초과] C:300GB / D:잔여", Color.FromArgb(30, 64, 175));
            btnTplOver512.Dock = DockStyle.Fill;
            btnTplOver512.Margin = new Padding(3);
            btnTplOver512.Click += (s, e) => ApplyTemplateOver512();

            var btnTplHalf512 = CreateButton("[512GB 이하] C:50% / D:50%", Color.FromArgb(2, 132, 199));
            btnTplHalf512.Dock = DockStyle.Fill;
            btnTplHalf512.Margin = new Padding(3);
            btnTplHalf512.Click += (s, e) => ApplyTemplateHalf512();

            var btnTplMulti = CreateButton("[다중 디스크] 최소용량 OS / 나머지 Data", Color.FromArgb(124, 58, 237));
            btnTplMulti.Dock = DockStyle.Fill;
            btnTplMulti.Margin = new Padding(3);
            btnTplMulti.Click += (s, e) => ApplyTemplateMultiDisk();

            tblTemplate.Controls.Add(btnTplSingleC, 0, 0);
            tblTemplate.Controls.Add(btnTplOver512, 1, 0);
            tblTemplate.Controls.Add(btnTplHalf512, 0, 1);
            tblTemplate.Controls.Add(btnTplMulti, 1, 1);
            grpTemplate.Controls.Add(tblTemplate);

            // Diskpart 스크립트 에디터
            var lblScript = new Label
            {
                Text = "DiskPart 실행 스크립트 (지능형 스마트 모드인 경우 자동 생성):",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(0, 10, 0, 0),
                ForeColor = Color.FromArgb(203, 213, 225)
            };
            _txtCommands = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                Height = 150,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(10, 14, 20),
                ForeColor = Color.FromArgb(125, 211, 252),
                Font = new Font("Consolas", 9.5f)
            };

            // 저장 버튼 바 (하단)
            var pnlSave = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(0, 14, 0, 0) };
            _btnSave = CreateButton("💾 권장양식 저장하기", Color.FromArgb(14, 165, 233));
            _btnSave.Size = new Size(200, 40);
            _btnSave.Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold);
            _btnSave.Click += (s, e) => SaveCurrentPolicy();
            pnlSave.Controls.Add(_btnSave);

            // ★ 핵심: WinForms에서 DockStyle.Top 컨트롤들은 역순으로 추가해야 위에서부터 순서대로 배치됩니다!
            pnlRight.Controls.Add(pnlSave);
            pnlRight.Controls.Add(_txtCommands);
            pnlRight.Controls.Add(lblScript);
            pnlRight.Controls.Add(grpTemplate);
            pnlRight.Controls.Add(formTable);
            pnlRight.Controls.Add(lblEditTitle);

            // 폼에 도킹 추가
            this.Controls.Add(pnlRight);
            this.Controls.Add(spl);
            this.Controls.Add(pnlLeft);
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

        private void LoadPolicyList()
        {
            _listPolicies.Items.Clear();
            foreach (var p in _configManager.PartitionPolicies)
            {
                _listPolicies.Items.Add(p.Name);
            }
            if (_listPolicies.Items.Count > 0)
            {
                _listPolicies.SelectedIndex = 0;
            }
        }

        private void ListPolicies_SelectedIndexChanged(object? sender, EventArgs e)
        {
            int idx = _listPolicies.SelectedIndex;
            if (idx >= 0 && idx < _configManager.PartitionPolicies.Count)
            {
                _currentSelected = _configManager.PartitionPolicies[idx];
                BindPolicyToForm(_currentSelected);
            }
        }

        private void BindPolicyToForm(PartitionPolicy p)
        {
            _txtName.Text = p.Name;
            _cboScheme.SelectedItem = p.PartitionScheme ?? "GPT";
            _txtDesc.Text = p.Description ?? "";
            _txtModel.Text = p.Conditions?.ModelPattern ?? "*";
            _numMinCap.Value = p.Conditions?.MinCapacityGb ?? 0;
            _numMaxCap.Value = p.Conditions?.MaxCapacityGb ?? 99999;
            _chkAutoApprove.Checked = p.AutoApprove;

            if (p.DiskpartCommands != null && p.DiskpartCommands.Count > 0)
            {
                _txtCommands.Text = string.Join(Environment.NewLine, p.DiskpartCommands);
            }
            else if (p.IsSmartDynamic || p.Id == "policy-smart-auto-gpt")
            {
                _txtCommands.Text = "# [지능형 자동 감지 분할 모드] 타깃 PC 디스크 수량 및 용량(512GB 기준 300GB / 50% 분할)에 따라 실시간 자동 생성됩니다.";
            }
            else
            {
                _txtCommands.Text = "";
            }
        }

        private void CreateNewPolicy()
        {
            _currentSelected = new PartitionPolicy
            {
                Id = "policy-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Name = "신규 파티션 권장양식",
                PartitionScheme = "GPT",
                Description = "권장 파티션 레이아웃 설정",
                AutoApprove = true,
                Conditions = new PolicyConditions { ModelPattern = "*", TargetDiskCriteria = "SMART_AUTO" }
            };
            ApplyTemplateOver512();
            _txtName.Focus();
        }

        private void ApplyTemplateSingleC()
        {
            _cboScheme.SelectedItem = "GPT";
            _txtCommands.Text = string.Join(Environment.NewLine, new[]
            {
                "select disk 0", "clean", "convert gpt",
                "create partition efi size=100", "format quick fs=fat32 label=\"System\"", "assign letter=\"S\"",
                "create partition msr size=16",
                "create partition primary", "format quick fs=ntfs label=\"Windows11\"", "assign letter=\"W\""
            });
        }

        private void ApplyTemplateOver512()
        {
            _cboScheme.SelectedItem = "GPT";
            _txtCommands.Text = string.Join(Environment.NewLine, new[]
            {
                "select disk 0", "clean", "convert gpt",
                "create partition efi size=100", "format quick fs=fat32 label=\"System\"", "assign letter=\"S\"",
                "create partition msr size=16",
                "create partition primary size=307200", "format quick fs=ntfs label=\"Windows11\"", "assign letter=\"W\"",
                "create partition primary", "format quick fs=ntfs label=\"Data\"", "assign letter=\"D\""
            });
        }

        private void ApplyTemplateHalf512()
        {
            _cboScheme.SelectedItem = "GPT";
            _txtCommands.Text = string.Join(Environment.NewLine, new[]
            {
                "select disk 0", "clean", "convert gpt",
                "create partition efi size=100", "format quick fs=fat32 label=\"System\"", "assign letter=\"S\"",
                "create partition msr size=16",
                "create partition primary size=245760", "format quick fs=ntfs label=\"Windows11\"", "assign letter=\"W\"",
                "create partition primary", "format quick fs=ntfs label=\"Data\"", "assign letter=\"D\""
            });
        }

        private void ApplyTemplateMultiDisk()
        {
            _cboScheme.SelectedItem = "GPT";
            _txtCommands.Text = string.Join(Environment.NewLine, new[]
            {
                "select disk 0", "clean", "convert gpt",
                "create partition efi size=100", "format quick fs=fat32 label=\"System\"", "assign letter=\"S\"",
                "create partition msr size=16",
                "create partition primary", "format quick fs=ntfs label=\"Windows11\"", "assign letter=\"W\"",
                "select disk 1", "clean", "convert gpt",
                "create partition primary", "format quick fs=ntfs label=\"Data\"", "assign letter=\"D\""
            });
        }

        private void SaveCurrentPolicy()
        {
            string name = _txtName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("양식 이름을 입력해 주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_currentSelected == null)
            {
                _currentSelected = new PartitionPolicy { Id = "policy-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                _configManager.PartitionPolicies.Add(_currentSelected);
            }

            _currentSelected.Name = name;
            _currentSelected.PartitionScheme = _cboScheme.SelectedItem?.ToString() ?? "GPT";
            _currentSelected.Description = _txtDesc.Text.Trim();
            _currentSelected.AutoApprove = _chkAutoApprove.Checked;

            _currentSelected.Conditions.ModelPattern = _txtModel.Text.Trim();
            _currentSelected.Conditions.MinCapacityGb = (int)_numMinCap.Value;
            _currentSelected.Conditions.MaxCapacityGb = (int)_numMaxCap.Value;

            string script = _txtCommands.Text.Trim();
            if (script.StartsWith("# [지능형"))
            {
                _currentSelected.IsSmartDynamic = true;
                _currentSelected.DiskpartCommands = null;
            }
            else
            {
                _currentSelected.IsSmartDynamic = false;
                _currentSelected.DiskpartCommands = script.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            if (!_configManager.PartitionPolicies.Contains(_currentSelected))
            {
                _configManager.PartitionPolicies.Add(_currentSelected);
            }

            _configManager.SavePartitionPolicies();
            LoadPolicyList();
            MessageBox.Show($"파티션 권장양식 '{name}'이(가) 저장되었습니다.", "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeleteCurrentPolicy()
        {
            if (_currentSelected == null) return;
            if (_configManager.PartitionPolicies.Count <= 1)
            {
                MessageBox.Show("최소 1개의 파티션 권장양식이 존재해야 합니다.", "삭제 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show($"정말로 '{_currentSelected.Name}' 권장양식을 삭제하시겠습니까?", "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _configManager.PartitionPolicies.Remove(_currentSelected);
                _configManager.SavePartitionPolicies();
                LoadPolicyList();
            }
        }
    }
}
