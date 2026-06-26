using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Udobl.Core;

namespace Udobl.UI
{
    /// <summary>Configuration UI: actions, behavior, exceptions, and live usage stats.</summary>
    public sealed class SettingsWindow : Window
    {
        public event Action<AppConfig> Applied;

        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x26));
        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x34));
        private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4));
        private static readonly Brush SubFg = new SolidColorBrush(Color.FromRgb(0xA0, 0xA8, 0xB4));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));

        private readonly AppConfig _config;
        private readonly UsageTracker _tracker;
        private readonly ObservableCollection<MenuItemConfig> _items;

        private DataGrid _grid;
        private CheckBox _cbGesture, _cbFill, _cbTrack, _cbUrls, _cbStartup, _cbUpdate;
        private Slider _slTilt, _slDead, _slSlots, _slHold;
        private TextBox _tbAccent, _tbUpdateUrl;
        private ListBox _lbExcept;
        private ComboBox _cbProc;
        private StackPanel _appsPanel, _urlsPanel;

        public SettingsWindow(AppConfig config, UsageTracker tracker)
        {
            _config = config;
            _tracker = tracker;
            _items = new ObservableCollection<MenuItemConfig>(_config.Items ?? new List<MenuItemConfig>());

            Title = "Udobl — Настройки";
            Width = 800;
            Height = 640;
            MinWidth = 640;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Fg;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            Build();
        }

        private void Build()
        {
            var root = new DockPanel { LastChildFill = true };

            // ---- bottom action bar ----
            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(14, 8, 14, 12) };
            var save = MakeButton("Сохранить", true);
            save.Click += (s, e) => Save();
            var close = MakeButton("Закрыть", false);
            close.Click += (s, e) => Close();
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(save);
            bar.Children.Add(close);

            var hint = new TextBlock
            {
                Text = "Удерживайте среднюю кнопку мыши, чтобы открыть кольцо · отпустите на секторе, чтобы выполнить.",
                Foreground = SubFg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(hint, Dock.Left);
            bar.Children.Add(hint);
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);

            var tabs = new TabControl { Background = Bg, BorderThickness = new Thickness(0), Margin = new Thickness(8) };
            tabs.Items.Add(new TabItem { Header = "Действия", Content = BuildActionsTab() });
            tabs.Items.Add(new TabItem { Header = "Поведение", Content = BuildBehaviorTab() });
            tabs.Items.Add(new TabItem { Header = "Подсказки", Content = BuildStatsTab() });
            root.Children.Add(tabs);

            Content = root;
        }

        // ---- Actions tab ----

        private UIElement BuildActionsTab()
        {
            var dock = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

            _grid = BuildGrid(_items);
            var toolbar = MakeToolbar(_grid, _items);
            DockPanel.SetDock(toolbar, Dock.Top);
            dock.Children.Add(toolbar);
            dock.Children.Add(_grid);

            var note = new TextBlock
            {
                Text = "Значок приложений и ссылок берётся прямо из Windows. «Подкруг» — вложенное кольцо: выберите его и нажмите «Изменить подкруг…», чтобы наполнить. В жесте: отпустите на подкруге, чтобы войти; отпустите в центре, чтобы вернуться назад.",
                Foreground = SubFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            DockPanel.SetDock(note, Dock.Bottom);
            dock.Children.Add(note);
            return dock;
        }

        private DataGrid BuildGrid(System.Collections.IList source)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = source,
                Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8)),
                RowBackground = Brushes.White,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                SelectionMode = DataGridSelectionMode.Single,
            };
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Вкл", Binding = new Binding("Enabled") { Mode = BindingMode.TwoWay }, Width = 40 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Значок", Binding = new Binding("Glyph") { Mode = BindingMode.TwoWay }, Width = 56, ElementStyle = IconCellStyle(typeof(TextBlock)), EditingElementStyle = IconCellStyle(typeof(TextBox)) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new Binding("Label") { Mode = BindingMode.TwoWay }, Width = 130 });
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Действие", Width = 160, CellTemplate = BuildKindComboTemplate() });
            grid.Columns.Add(new DataGridTextColumn { Header = "Цель (путь / ссылка / команда / клавиши)", Binding = new Binding("Target") { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Аргументы", Binding = new Binding("Args") { Mode = BindingMode.TwoWay }, Width = 110 });
            return grid;
        }

        private StackPanel MakeToolbar(DataGrid grid, ObservableCollection<MenuItemConfig> items)
        {
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var add = MakeButton("Добавить", false);
            add.Click += (s, e) =>
            {
                items.Add(new MenuItemConfig { Label = "Новое действие", Glyph = ConfigStore.Ico(0xE700), Kind = (int)ActionKind.LaunchApp });
                grid.SelectedIndex = items.Count - 1; grid.ScrollIntoView(items[items.Count - 1]);
            };
            var addGroup = MakeButton("Подкруг", false);
            addGroup.Click += (s, e) =>
            {
                items.Add(new MenuItemConfig { Label = "Новый подкруг", Glyph = ConfigStore.Ico(0xE8B7), Kind = (int)ActionKind.Group, Children = new List<MenuItemConfig>() });
                grid.SelectedIndex = items.Count - 1; grid.ScrollIntoView(items[items.Count - 1]);
            };
            var edit = MakeButton("Изменить подкруг…", false);
            edit.Click += (s, e) => { var it = grid.SelectedItem as MenuItemConfig; if (it != null && it.IsGroup) OpenGroupEditor(it); };
            var del = MakeButton("Удалить", false);
            del.Click += (s, e) => { int i = grid.SelectedIndex; if (i >= 0 && i < items.Count) items.RemoveAt(i); };
            var up = MakeButton("↑", false); up.Click += (s, e) => MoveIn(grid, items, -1);
            var down = MakeButton("↓", false); down.Click += (s, e) => MoveIn(grid, items, 1);
            foreach (var b in new[] { add, addGroup, edit, del, up, down }) toolbar.Children.Add(b);
            return toolbar;
        }

        private static void MoveIn(DataGrid grid, ObservableCollection<MenuItemConfig> items, int delta)
        {
            int i = grid.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= items.Count) return;
            items.Move(i, j); grid.SelectedIndex = j;
        }

        private void OpenGroupEditor(MenuItemConfig group)
        {
            if (group.Children == null) group.Children = new List<MenuItemConfig>();
            var coll = new ObservableCollection<MenuItemConfig>(group.Children);
            var grid = BuildGrid(coll);
            var toolbar = MakeToolbar(grid, coll);

            var dock = new DockPanel { Margin = new Thickness(10), LastChildFill = true };
            DockPanel.SetDock(toolbar, Dock.Top);
            dock.Children.Add(toolbar);
            dock.Children.Add(grid);

            var win = new Window
            {
                Title = "Подкруг: " + group.Label,
                Width = 720,
                Height = 500,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Bg,
                Foreground = Fg,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Content = dock,
            };
            win.Closed += (s, e) =>
            {
                try { grid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
                group.Children = coll.ToList(); // recursion: nested groups keep their own Children references
            };
            win.ShowDialog();
        }

        private static Style IconCellStyle(Type targetType)
        {
            var font = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var style = new Style(targetType);
            if (targetType == typeof(TextBlock))
            {
                style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, font));
                style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 17.0));
                style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            }
            else
            {
                style.Setters.Add(new Setter(Control.FontFamilyProperty, font));
                style.Setters.Add(new Setter(Control.FontSizeProperty, 17.0));
            }
            return style;
        }

        private DataTemplate BuildKindComboTemplate()
        {
            var f = new FrameworkElementFactory(typeof(ComboBox));
            f.SetValue(ComboBox.ItemsSourceProperty, new[] { "Запустить приложение", "Открыть ссылку", "Выполнить команду", "Горячая клавиша", "Подкруг (группа)" });
            f.SetValue(ComboBox.MarginProperty, new Thickness(1));
            f.SetBinding(ComboBox.SelectedIndexProperty,
                new Binding("Kind") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return new DataTemplate { VisualTree = f };
        }


        // ---- Behavior tab ----

        private UIElement BuildBehaviorTab()
        {
            var stack = new StackPanel { Margin = new Thickness(16) };

            stack.Children.Add(Header("Жест"));
            _cbGesture = Check("Включить жест средней кнопкой мыши", _config.Settings.GestureEnabled);
            _cbStartup = Check("Запускать Udobl при старте Windows", _config.Settings.RunOnStartup);
            _cbFill = Check("Заполнять свободные секторы частыми действиями (подсказками)", _config.Settings.FillWithSuggestions);
            stack.Children.Add(_cbGesture);
            stack.Children.Add(_cbStartup);
            stack.Children.Add(_cbFill);

            stack.Children.Add(Header("Отслеживание"));
            _cbTrack = Check("Отслеживать, какими приложениями я пользуюсь (для подсказок)", _config.Settings.TrackUsage);
            _cbUrls = Check("Также отслеживать посещаемые сайты в браузерах (через UI Automation)", _config.Settings.TrackUrls);
            stack.Children.Add(_cbTrack);
            stack.Children.Add(_cbUrls);

            stack.Children.Add(Header("Вид и поведение"));
            _slTilt = SliderRow(stack, "3D наклон", 0, 55, _config.Settings.TiltDegrees, "°");
            _slDead = SliderRow(stack, "Мёртвая зона в центре", 30, 130, _config.Settings.DeadZoneRadius, " px");
            _slSlots = SliderRow(stack, "Макс. секторов", 3, 14, _config.Settings.MaxSlots, "");
            _slHold = SliderRow(stack, "Задержка до открытия", 60, 500, _config.Settings.HoldMs, " мс");

            var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            accentRow.Children.Add(new TextBlock { Text = "Акцентный цвет", Foreground = Fg, Width = 200, VerticalAlignment = VerticalAlignment.Center });
            _tbAccent = new TextBox { Text = _config.Settings.AccentColor, Width = 120 };
            accentRow.Children.Add(_tbAccent);
            stack.Children.Add(accentRow);

            stack.Children.Add(Header("Обновления"));
            _cbUpdate = Check("Проверять обновления автоматически", _config.Settings.UpdateCheckEnabled);
            stack.Children.Add(_cbUpdate);
            var updRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            updRow.Children.Add(new TextBlock { Text = "Адрес version.json", Foreground = Fg, Width = 200, VerticalAlignment = VerticalAlignment.Center });
            _tbUpdateUrl = new TextBox { Text = _config.Settings.UpdateUrl, Width = 360 };
            updRow.Children.Add(_tbUpdateUrl);
            stack.Children.Add(updRow);
            stack.Children.Add(new TextBlock
            {
                Text = "Укажите ссылку на ваш version.json — Udobl сравнит версию и уведомит, если есть новее (без авто-скачивания). Проверить вручную можно из меню в трее.",
                Foreground = SubFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });

            stack.Children.Add(Header("Исключения (здесь средняя кнопка работает как обычно)"));
            var exRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
            _lbExcept = new ListBox
            {
                Height = 120,
                Background = Panel,
                Foreground = Fg,
                BorderThickness = new Thickness(1),
            };
            foreach (var p in _config.Settings.ExcludedProcesses) _lbExcept.Items.Add(p);

            var exControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _cbProc = new ComboBox { Width = 240, IsEditable = true };
            foreach (var n in RunningProcessNames()) _cbProc.Items.Add(n);
            var addEx = MakeButton("Добавить", false); addEx.Click += (s, e) => AddException();
            var delEx = MakeButton("Удалить", false); delEx.Click += (s, e) => { if (_lbExcept.SelectedItem != null) _lbExcept.Items.Remove(_lbExcept.SelectedItem); };
            exControls.Children.Add(_cbProc);
            exControls.Children.Add(addEx);
            exControls.Children.Add(delEx);

            DockPanel.SetDock(exControls, Dock.Bottom);
            var exWrap = new StackPanel();
            exWrap.Children.Add(_lbExcept);
            exWrap.Children.Add(exControls);
            stack.Children.Add(exWrap);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
            return scroll;
        }

        private void AddException()
        {
            string name = (_cbProc.Text ?? "").Trim().ToLowerInvariant();
            if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
            if (name.Length == 0 || _lbExcept.Items.Contains(name)) return;
            _lbExcept.Items.Add(name);
            _cbProc.Text = "";
        }

        private static IEnumerable<string> RunningProcessNames()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.ProcessName)) set.Add(p.ProcessName.ToLowerInvariant()); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return set;
        }

        // ---- Suggestions / Stats tab ----

        private UIElement BuildStatsTab()
        {
            var dock = new DockPanel { Margin = new Thickness(14), LastChildFill = true };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var refresh = MakeButton("Обновить", false); refresh.Click += (s, e) => RefreshStats();
            var reset = MakeButton("Сбросить статистику", false); reset.Click += (s, e) => { _tracker.Reset(); RefreshStats(); };
            top.Children.Add(refresh);
            top.Children.Add(reset);
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            left.Children.Add(Header("Частые приложения"));
            _appsPanel = new StackPanel();
            left.Children.Add(_appsPanel);
            Grid.SetColumn(left, 0);

            var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            right.Children.Add(Header("Частые сайты"));
            _urlsPanel = new StackPanel();
            right.Children.Add(_urlsPanel);
            Grid.SetColumn(right, 1);

            cols.Children.Add(left);
            cols.Children.Add(right);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = cols };
            dock.Children.Add(scroll);

            RefreshStats();
            return dock;
        }

        private void RefreshStats()
        {
            _appsPanel.Children.Clear();
            _urlsPanel.Children.Clear();

            var apps = _tracker.TopApps(15);
            if (apps.Count == 0) _appsPanel.Children.Add(EmptyNote("Пока нет данных — просто пользуйтесь ПК."));
            foreach (var e in apps)
                _appsPanel.Children.Add(StatRow(e.Display, (int)Math.Round(e.Count), () => PinAndNotify(Suggestions.FromApp(e))));

            var urls = _tracker.TopUrls(15);
            if (urls.Count == 0) _urlsPanel.Children.Add(EmptyNote("Сайты пока не отслеживались."));
            foreach (var e in urls)
                _urlsPanel.Children.Add(StatRow(e.Display, (int)Math.Round(e.Count), () => PinAndNotify(Suggestions.FromUrl(e))));
        }

        private void PinAndNotify(MenuItemConfig item)
        {
            if (_items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase))) return;
            _items.Add(item);
        }

        private FrameworkElement StatRow(string label, int count, Action onPin)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
            var pin = MakeButton("Закрепить", false);
            pin.Click += (s, e) => onPin();
            DockPanel.SetDock(pin, Dock.Right);
            row.Children.Add(pin);

            var cnt = new TextBlock { Text = count.ToString(), Foreground = SubFg, Width = 46, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cnt, Dock.Right);
            row.Children.Add(cnt);

            var txt = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(txt);

            var border = new Border { Background = Panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 2, 0, 2), Child = row };
            return border;
        }

        private FrameworkElement EmptyNote(string text)
        {
            return new TextBlock { Text = text, Foreground = SubFg, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        }

        // ---- save ----

        private void Save()
        {
            try { _grid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

            var cfg = new AppConfig();
            cfg.Items = _items.ToList();

            var s = cfg.Settings;
            s.GestureEnabled = _cbGesture.IsChecked == true;
            s.RunOnStartup = _cbStartup.IsChecked == true;
            s.FillWithSuggestions = _cbFill.IsChecked == true;
            s.TrackUsage = _cbTrack.IsChecked == true;
            s.TrackUrls = _cbUrls.IsChecked == true;
            s.TiltDegrees = Math.Round(_slTilt.Value);
            s.DeadZoneRadius = (int)Math.Round(_slDead.Value);
            s.MaxSlots = (int)Math.Round(_slSlots.Value);
            s.HoldMs = (int)Math.Round(_slHold.Value);
            s.AccentColor = string.IsNullOrWhiteSpace(_tbAccent.Text) ? "#4FC3F7" : _tbAccent.Text.Trim();
            s.ExcludedProcesses = _lbExcept.Items.Cast<string>().ToList();
            s.UpdateCheckEnabled = _cbUpdate.IsChecked == true;
            s.UpdateUrl = (_tbUpdateUrl.Text ?? "").Trim();

            // Carry over settings that have no UI control so they aren't reset on save.
            s.LastUpdateCheck = _config.Settings.LastUpdateCheck;
            s.MaxDepth = _config.Settings.MaxDepth;
            s.ContextMenuEnabled = _config.Settings.ContextMenuEnabled;

            Applied?.Invoke(cfg);
            Close();
        }

        // ---- small UI helpers ----

        private static TextBlock Header(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Accent,
            Margin = new Thickness(0, 16, 0, 6),
        };

        private CheckBox Check(string text, bool value) => new CheckBox
        {
            Content = text,
            IsChecked = value,
            Foreground = Fg,
            Margin = new Thickness(0, 4, 0, 0),
        };

        private Slider SliderRow(Panel parent, string label, double min, double max, double value, string suffix)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Foreground = Fg, Width = 200, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 320, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock { Foreground = SubFg, Width = 70, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            val.Text = Math.Round(slider.Value) + suffix;
            slider.ValueChanged += (s, e) => val.Text = Math.Round(slider.Value) + suffix;
            row.Children.Add(slider);
            row.Children.Add(val);
            parent.Children.Add(row);
            return slider;
        }

        private static Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 64,
                Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46)),
                Foreground = primary ? Brushes.Black : Fg,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }
    }
}
