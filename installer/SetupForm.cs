using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveClipper.Installation
{
    public sealed class SetupForm:Form
    {
        readonly TextBox directory=new TextBox();
        readonly LogEdit log=new LogEdit();
        readonly Label status=new Label(),detail=new Label(),percent=new Label(),notice=new Label(),subtitle=new Label();
        readonly CheckBox desktop=new CheckBox(),menu=new CheckBox(),removeData=new CheckBox();
        readonly Button run=new SetupButton(ActionStyle.Primary),cancel=new SetupButton(ActionStyle.Secondary),browse=new SetupButton(ActionStyle.Secondary);
        readonly ThemeProgress progressBar=new ThemeProgress();
        readonly bool uninstall;readonly string root;readonly bool explicitRoot;bool busy,completed,updating;
        CancellationTokenSource cancellation;Installation installed;SourcePolicy detected=new SourcePolicy();

        public SetupForm(bool uninstall=false,string root=null,bool proof=false)
        {
            this.uninstall=uninstall;explicitRoot=root!=null;this.root=root;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = uninstall ? "卸载 LiveClipper" : "安装 LiveClipper";
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = Theme.Text;
            Theme.FrameWindow(this);
            ClientSize = new Size(920, 620);
            MinimumSize = new Size(600, 450);
            StartPosition = FormStartPosition.CenterScreen;
            if (!proof) ClientSize = new Size(920, Math.Min(620, Screen.PrimaryScreen.WorkingArea.Height - 70));
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);

            var page = Grid(1, 3, Theme.Background);
            page.Dock = DockStyle.Fill;
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(BuildHeader(), 0, 0);
            page.Controls.Add(BuildBody(), 0, 1);
            page.Controls.Add(BuildFooter(), 0, 2);
            Controls.Add(page);
            ResumeLayout(true);

            browse.Click+=(s,e)=>{using(var picker=new FolderBrowserDialog{Description="选择存放 LiveClipper 的位置",SelectedPath=Path.GetDirectoryName(directory.Text)})if(picker.ShowDialog(this)==DialogResult.OK)directory.Text=Path.Combine(picker.SelectedPath,"LiveClipper");};
            cancel.Click+=(s,e)=>{if(busy&&!uninstall){cancellation.Cancel();cancel.Enabled=false;status.Text="正在取消，请稍候…";}else if(!busy)Close();};
            run.Click+=async(s,e)=>await Execute();
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;if(!uninstall)cancellation.Cancel();}};
            if(!proof)Shown+=async(s,e)=>{if(!uninstall)await Scan();};
        }

        static TableLayoutPanel Grid(int columns, int rows, Color background)
        {
            return new TableLayoutPanel {
                ColumnCount = columns, RowCount = rows, BackColor = background,
                Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty,
                TabStop = false, GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
        }

        Label Caption(string text, float size, Color color, bool bold = false)
        {
            var label = new Label();
            SetCaption(label, text, size, color, bold);
            return label;
        }

        void SetCaption(Label label, string text, float size, Color color, bool bold = false)
        {
            label.Text = text;
            label.UseMnemonic = false;
            label.Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Margin = Padding.Empty;
        }

        Control BuildHeader()
        {
            var hero = Grid(3, 1, Theme.Frame);
            hero.Padding = new Padding(24, 6, 24, 14);
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76));
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
            using (var logo = Image.FromStream(stream)) {
                hero.Controls.Add(new PictureBox {
                    Image = new Bitmap(logo), SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(58, 58), Anchor = AnchorStyles.Left, Margin = Padding.Empty,
                    AccessibleName = "LiveClipper", TabStop = false
                }, 0, 0);
            }
            var titles = Grid(1, 3, hero.BackColor);
            titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            titles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            titles.Controls.Add(Caption(uninstall ? "卸载向导" : "安装向导", 9, Theme.AccentText, true), 0, 0);
            titles.Controls.Add(Caption("LiveClipper", 23, Theme.Text, true), 0, 1);
            SetCaption(subtitle, uninstall ? "移除软件与专用环境，可保留编辑数据。" :
                "轻量安装 · 下载线路自动选择，可选组件在软件设置中按需补充", 10, Theme.Secondary);
            titles.Controls.Add(subtitle, 0, 2);
            hero.Controls.Add(titles, 1, 0);
            hero.Controls.Add(new SignalArt { Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) }, 2, 0);
            return hero;
        }

        Control BuildBody()
        {
            var viewport = new ContentViewport { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Background };
            var stack = Grid(1, 2, Theme.Background);
            stack.Dock = DockStyle.Top;
            stack.Padding = new Padding(20, 14, 20, 12);
            stack.MinimumSize = new Size(0, uninstall ? 345 : 318);
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stack.Controls.Add(BuildOptions(), 0, 0);
            stack.Controls.Add(BuildLog(), 0, 1);
            viewport.Controls.Add(stack);
            return viewport;
        }

        Control BuildOptions()
        {
            // One host owns every native HWND. No auto-sized table can grow the location row
            // under the following options row (or clip its edit and browse button together).
            var card = new LocationCard { Dock = DockStyle.Top, Margin = Padding.Empty, TabIndex = 0 };
            var heading = Caption("安装位置", 11, Theme.Text, true);
            heading.Dock = DockStyle.None;
            heading.BackColor = Theme.Surface;
            card.Controls.Add(heading);

            directory.AccessibleName = "安装位置";
            directory.TabIndex = 0;
            directory.AutoSize = true;
            directory.BackColor = Theme.Well;
            directory.ForeColor = Theme.Text;
            directory.BorderStyle = BorderStyle.None;
            directory.Text=root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\LiveClipper");
            directory.ReadOnly=uninstall;
            directory.Enter += (s,e) => { card.FieldFocused = true; card.Invalidate(); };
            directory.Leave += (s,e) => { card.FieldFocused = false; card.Invalidate(); };
            card.Controls.Add(directory);
            browse.Text = "浏览…";
            browse.AccessibleName = "浏览安装位置";
            browse.TabIndex = 1;
            browse.Enabled = !uninstall;
            card.Controls.Add(browse);

            Label message;
            if (!uninstall) {
                SetCaption(notice, "", 9.5F, Theme.AccentText, true);
                notice.Visible = false;
                message = notice;
                card.Controls.Add(Option(desktop, "创建桌面快捷方式", true));
                card.Controls.Add(Option(menu, "创建开始菜单与卸载入口", true));
                desktop.TabIndex = 2; menu.TabIndex = 3;
            } else {
                message = Caption("卸载会关闭此目录中的程序并移除专用环境。\n外部原始素材与共享环境会保留。默认保留 data 文件夹，重新安装后可继续使用。", 10, Theme.Secondary);
                card.Controls.Add(Option(removeData, "同时删除此安装目录中的全部软件数据（无法恢复）", false));
                removeData.TabIndex = 2;
            }
            message.Dock = DockStyle.None;
            message.BackColor = Theme.Surface;
            message.AutoEllipsis = false;
            card.Controls.Add(message);

            int lastWidth = -1;
            void Arrange()
            {
                lastWidth = card.ClientSize.Width;
                int D(int pixels) => (int)Math.Round(pixels * card.DeviceDpi / 96f);
                var left = D(20);
                var width = Math.Max(1, lastWidth - 2 * left);
                var y = D(12);
                int titleHeight = Math.Max(D(24), heading.Font.Height + D(4));
                heading.SetBounds(left, y, width, titleHeight);
                y += titleHeight + D(5);

                // Use the edit's real native height and the button's actual font height.
                var editHeight = directory.PreferredHeight;
                var rowHeight = Math.Max(D(40), Math.Max(editHeight + D(14), browse.Font.Height + D(14)));
                var buttonWidth = Math.Max(D(102), TextRenderer.MeasureText(browse.Text, browse.Font).Width + D(28));
                var fieldWidth = Math.Max(1, width - buttonWidth - D(10));
                card.FieldBounds = new Rectangle(left, y, fieldWidth, rowHeight);
                directory.SetBounds(left + D(12), y + (rowHeight - editHeight) / 2,
                    Math.Max(1, fieldWidth - D(24)), editHeight);
                browse.SetBounds(left + fieldWidth + D(10), y, buttonWidth, rowHeight);
                y += rowHeight + D(10);

                if (message.Text.Length != 0) {
                    var measure = TextRenderer.MeasureText(message.Text, message.Font, new Size(width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    var h = measure.Height + D(4);
                    message.SetBounds(left, y, width, h);
                    y += h + D(4);
                }

                var first = uninstall ? removeData : desktop;
                var firstSize = first.GetPreferredSize(Size.Empty);
                first.SetBounds(left, y, firstSize.Width, firstSize.Height);
                var bottom = first.Bottom;
                if (!uninstall) {
                    var nextSize = menu.GetPreferredSize(Size.Empty);
                    var nextX = first.Right + D(25);
                    var nextY = y;
                    if (nextX + nextSize.Width > left + width) { nextX = left; nextY = bottom + D(5); }
                    menu.SetBounds(nextX, nextY, nextSize.Width, nextSize.Height);
                    bottom = Math.Max(bottom, menu.Bottom);
                }
                var height = bottom + D(18);
                card.MinimumSize = new Size(0, height);
                card.Height = height;
                card.Invalidate();
            }
            card.SizeChanged += (s,e) => { if (card.ClientSize.Width != lastWidth) Arrange(); };
            directory.FontChanged += (s,e) => Arrange();
            card.DpiChangedAfterParent += (s,e) => Arrange();
            message.TextChanged += (s,e) => Arrange();
            Arrange();
            return card;
        }

        CheckBox Option(CheckBox box, string text, bool check)
        {
            box.Text = text;
            box.Checked = check;
            box.AutoSize = true;
            box.ForeColor = Theme.Text;
            box.BackColor = Theme.Surface;
            box.FlatStyle = FlatStyle.Standard;
            box.Cursor = Cursors.Hand;
            box.Margin = new Padding(0, 3, 24, 3);
            return box;
        }

        Control BuildLog()
        {
            var block = Grid(1, 2, Theme.Well);
            block.Margin = new Padding(0, 10, 0, 0);
            block.Padding = new Padding(14, 5, 12, 9);
            block.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            block.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            block.Controls.Add(Caption("诊断日志", 10, Theme.Secondary, true), 0, 0);
            StyleLog(log);
            block.Controls.Add(new LogSurface(log), 0, 1);
            return block;
        }

        void StyleLog(TextBox text)
        {
            text.Multiline = true;
            text.ReadOnly = true;
            text.ScrollBars = ScrollBars.Both;
            text.WordWrap = false;
            text.BorderStyle = BorderStyle.None;
            text.BackColor = Theme.Well;
            text.ForeColor = Theme.Secondary;
            text.Font = new Font(Font.FontFamily, 9.5F);
            text.AccessibleName = "诊断日志";
            text.Dock = DockStyle.Fill;
            text.Margin = Padding.Empty;
        }

        Control BuildFooter()
        {
            var strip = Grid(1, 4, Theme.Background);
            strip.AutoSize = true;
            strip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            strip.Padding = new Padding(20, 8, 20, 12);
            strip.Paint += (s,e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, strip.Width, 0); };
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));

            var meter = Grid(2, 1, strip.BackColor);
            meter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            meter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            SetCaption(status, uninstall ? "确认位置后点击卸载。" : "正在检测本机环境…", 11, Theme.Text, true);
            SetCaption(percent, "0%", 12, Theme.AccentText, true);
            percent.TextAlign = ContentAlignment.MiddleRight;
            meter.Controls.Add(status, 0, 0);
            meter.Controls.Add(percent, 1, 0);
            strip.Controls.Add(meter, 0, 0);
            SetCaption(detail, uninstall ? "编辑数据默认保留。" : "下载线路与环境检测在后台自动完成。", 9.5F, Theme.Secondary);
            strip.Controls.Add(detail, 0, 1);
            progressBar.Dock = DockStyle.Fill;
            progressBar.Margin = new Padding(0, 2, 0, 6);
            strip.Controls.Add(progressBar, 0, 2);

            var actions = Grid(4, 1, strip.BackColor);
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 97));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            actions.Controls.Add(Caption(uninstall ? "移除程序  →  清理专用环境  →  完成" :
                "准备  →  主程序  →  运行环境  →  完成", 9, Theme.Secondary), 0, 0);
            var details = new SetupButton(ActionStyle.Quiet) { Text = "查看详情" };
            details.Click += (s,e) => ShowDetails();
            cancel.Text = "关闭";
            run.Text = uninstall ? "卸载软件" : "开始安装";
            run.Font = new Font(Font, FontStyle.Bold);
            int col = 1;
            foreach (var button in new Button[]{ details, cancel, run }) {
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(6, 6, 0, 1);
                actions.Controls.Add(button, col++, 0);
            }
            strip.Controls.Add(actions, 0, 3);
            return strip;
        }

        /// <summary>后台检测：地区（线路自动切换）、本机环境、已有安装。结果只进日志，不占界面。</summary>
        async Task Scan()
        {
            var regionTask=SourcePolicy.Detect(CancellationToken.None);
            var environmentTask=Task.Run(Engine.Diagnose);
            // 已安装识别：显式传了 --dir 就尊重调用方，否则查默认目录与当前用户卸载注册表。
            var existingTask=explicitRoot||uninstall?Task.FromResult<Installation>(null):Task.Run(Engine.FindExistingInstall);
            try{await Task.WhenAll(regionTask,environmentTask,existingTask);}catch{}
            if(IsDisposed)return;
            if(regionTask.Status==TaskStatus.RanToCompletion)detected=regionTask.Result;
            log.AppendText("环境检测（后台）"+Environment.NewLine+string.Join(Environment.NewLine,environmentTask.Result.Select(x=>x.Key+"："+x.Value))+Environment.NewLine);
            log.AppendText("下载线路：按公网 IP 自动切换（"+detected.Description+"）"+Environment.NewLine);
            if(existingTask.Status==TaskStatus.RanToCompletion&&existingTask.Result!=null)
            {
                var existing=existingTask.Result;updating=true;
                directory.Text=existing.Root;
                notice.Text="检测到已安装的 LiveClipper v"+existing.Version+"，将自动升级替换主程序（数据与已装组件保留）";
                notice.Visible=true;subtitle.Text="检测到已安装，正在准备升级替换";
                run.Text="升级替换";status.Text="检测到已安装，点击「升级替换」开始。";
                log.AppendText("已发现现有安装："+existing.Root+"（v"+existing.Version+"，状态 "+existing.State+"），进入升级模式"+Environment.NewLine);
            }
            else if(!uninstall)status.Text="准备安装";
        }
        public void SetProofDiagnostics()
        {
            log.AppendText("环境检测（示例）"+Environment.NewLine+string.Join(Environment.NewLine,Engine.Diagnose().Select(x=>x.Key+"："+x.Value))+Environment.NewLine);
            detected=new SourcePolicy{Country="CN"};
            log.AppendText("下载线路：按公网 IP 自动切换（"+detected.Description+"）"+Environment.NewLine);
        }
        void UpdateProgress(ProgressUpdate value)
        {
            if(IsDisposed)return;status.Text=value.Message;detail.Text=value.Detail??("当前步骤："+(value.Stage??"处理"));
            if(value.Percent>=0){progressBar.Value=value.Percent;percent.Text=progressBar.Value+"%";}
            if(value.Log){log.AppendText(value.Message+(value.Detail!=null?" · "+value.Detail:"")+Environment.NewLine);if(log.TextLength>80000)log.Text=log.Text.Substring(log.TextLength-50000);}
        }
        void ShowDetails()
        {
            using (var dialog = new Form {
                Text = "LiveClipper · 安装详情", StartPosition = FormStartPosition.CenterParent,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(810, 440), MinimumSize = new Size(480, 280),
                ForeColor = Theme.Text, Font = Font, Icon = Icon
            }) {
                Theme.FrameWindow(dialog);
                var panel = Grid(1, 2, Theme.Well);
                panel.Padding = new Padding(18, 8, 18, 14);
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.Controls.Add(Caption("诊断日志", 12, Theme.Text, true), 0, 0);
                var copy = new LogEdit();
                StyleLog(copy);
                copy.Text = log.Text;
                panel.Controls.Add(new LogSurface(copy), 0, 1);
                dialog.Controls.Add(panel);
                dialog.ShowDialog(this);
            }
        }

        async Task Execute()
        {
            if(completed){if(installed!=null)System.Diagnostics.Process.Start(Paths.LaunchInfo(installed));Close();return;}
            busy=true;run.Enabled=false;browse.Enabled=directory.Enabled=false;
            desktop.Enabled=menu.Enabled=removeData.Enabled=false;
            cancel.Text=uninstall?"请稍候":"取消安装";cancel.Enabled=!uninstall;cancellation=new CancellationTokenSource();
            var updates=new Progress<ProgressUpdate>(UpdateProgress);
            var autoClose=false;
            try
            {
                if(uninstall){var deleteData=removeData.Checked;await Task.Run(()=>Engine.Uninstall(root,deleteData,updates));}
                else
                {
                    // 组件策略：语音识别与剪映草稿是默认必需，随安装自动下载；本机 AI 与人声分离移到软件设置里按需获取。
                    var options=new InstallOptions{Root=directory.Text,Speech=true,LocalAi=false,Demucs=false,DesktopShortcut=desktop.Checked,StartMenu=menu.Checked,DownloadRoute="AUTO",DetectedCountry=detected.Country};
                    installed=await Task.Run(()=>new Engine(updates,cancellation.Token).Install(options));
                }
                completed=true;run.Enabled=true;progressBar.Value=100;percent.Text="100%";
                if(!uninstall&&installed!=null)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(Paths.LaunchInfo(installed));
                        autoClose=true;run.Text="已启动";status.Text=updating?"升级完成，已自动启动软件。":"安装完成，已自动启动软件。";detail.Text="LiveClipper 正在启动…";
                    }
                    catch(Exception launchError)
                    {
                        run.Text="重试启动";status.Text=updating?"升级完成，但自动启动失败。":"安装完成，但自动启动失败。";detail.Text=launchError.Message;log.AppendText("自动启动失败："+launchError+Environment.NewLine);
                    }
                }
                else{run.Text=uninstall?"完成":"启动软件";status.Text=uninstall?"卸载完成":"安装完成，可以启动软件。";detail.Text=installed?.Root??"操作完成";}
            }
            catch(OperationCanceledException){status.Text="安装已取消";detail.Text="重新选择相同目录可继续；已下载进度会保留。";run.Text=updating?"继续升级":"继续安装";run.Enabled=true;}
            catch(Exception ex){status.Text="操作未完成，可重试";detail.Text=ex.Message;log.AppendText(ex+Environment.NewLine);run.Text=uninstall?"重试卸载":(updating?"重试升级":"重试安装");run.Enabled=true;}
            finally
            {
                busy=false;cancel.Enabled=true;cancel.Text="关闭";cancellation.Dispose();
                if(!completed&&!uninstall){browse.Enabled=directory.Enabled=true;desktop.Enabled=menu.Enabled=true;}
                try{if(!uninstall)Paths.Load(directory.Text);var folder=uninstall?Path.Combine(Path.GetTempPath(),"LiveClipper-Logs"):Path.Combine(Paths.Full(directory.Text),"cache");if(Directory.Exists(folder)||uninstall){Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,uninstall?"uninstall.log":"install.log"),log.Text);}}catch{}
                if(autoClose){var closeTimer=new System.Windows.Forms.Timer{Interval=350};closeTimer.Tick+=(s,e)=>{closeTimer.Stop();closeTimer.Dispose();Close();};closeTimer.Start();}
            }
        }
    }
}
