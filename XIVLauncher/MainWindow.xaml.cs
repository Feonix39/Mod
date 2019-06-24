using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdysTech.CredentialManager;
using AutoUpdaterDotNET;
using MaterialDesignThemes.Wpf;
using XIVLauncher.Addon;
using Color = System.Windows.Media.Color;

namespace XIVLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private System.Timers.Timer _bannerChangeTimer;
        private Headlines _headlines;
        private BitmapImage[] _bannerBitmaps;
        private int _currentBannerIndex = 0;

        private System.Timers.Timer _maintenanceQueueTimer;

        public const string AppName = "FINAL FANTASY XIV";

        private XIVGame _game = new XIVGame();

        private bool isLoggingIn = false;

        public MainWindow()
        {
            InitializeComponent();

            this.Visibility = Visibility.Hidden;

            // Check if dark mode is enabled on windows, if yes, load the dark theme
            var themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml", UriKind.RelativeOrAbsolute);
            if (Util.IsWindowsDarkModeEnabled())
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml", UriKind.RelativeOrAbsolute);

            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary() { Source = themeUri });

#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                new ErrorWindow((Exception) args.ExceptionObject, "An unhandled exception occured.", "Unhandled").ShowDialog();
                isLoggingIn = false;
            };

            AutoUpdater.ShowSkipButton = false;
            AutoUpdater.ShowRemindLaterButton = false;
            AutoUpdater.Mandatory = true;
            AutoUpdater.UpdateMode = Mode.Forced;

            AutoUpdater.CheckForUpdateEvent += AutoUpdaterOnCheckForUpdateEvent;

            AutoUpdater.Start("https://goaaats.github.io/ffxiv/tools/launcher/update.xml");
#else
            InitializeWindow();
#endif

            this.Title += " v" + Util.GetAssemblyVersion();
        }

        private void InitializeWindow()
        {
            // Upgrade the stored settings if needed
            if (Properties.Settings.Default.UpgradeRequired)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpgradeRequired = false;
                Properties.Settings.Default.Save();
            }

            var gateStatus = false;
            try
            {
                gateStatus = _game.GetGateStatus();
            }
            catch
            {
                // ignored
            }

            if (!gateStatus)
            {
                WorldStatusPackIcon.Foreground = new SolidColorBrush(Color.FromRgb(242, 24, 24));
            }

            var savedCredentials = CredentialManager.GetCredentials(AppName);

            if (savedCredentials != null)
            {
                LoginUsername.Text = savedCredentials.UserName;
                LoginPassword.Password = savedCredentials.Password;
                OtpCheckBox.IsChecked = Settings.NeedsOtp();
                AutoLoginCheckBox.IsChecked = Settings.IsAutologin();
                SaveLoginCheckBox.IsChecked = true;
            }

            if (Settings.IsAutologin() && savedCredentials != null && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                try
                {
                    if (!gateStatus)
                    {
                        MessageBox.Show(
                            "Square Enix seems to be running maintenance work right now. The game shouldn't be launched.");
                        Settings.SetAutologin(false);
                    }
                    else
                    {
                        HandleLogin(true);
                    }
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "Additionally, please check your login information or try again.", "AutoLogin").ShowDialog();
                    Settings.SetAutologin(false);
                }

                Settings.Save();
            }

            if (Settings.GetGamePath() == string.Empty)
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();
            }

            Action setupHeadlines = (() => {
                try
                {
                    _headlines = Headlines.Get();

                    _bannerBitmaps = new BitmapImage[_headlines.Banner.Length];
                    for (int i = 0; i < _headlines.Banner.Length; i++)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = _headlines.Banner[i].LsbBanner;
                        bitmap.EndInit();

                        _bannerBitmaps[i] = bitmap;
                    }

                    BannerImage.Source = _bannerBitmaps[0];

                    _bannerChangeTimer = new System.Timers.Timer
                    {
                        Interval = 5000
                    };

                    _bannerChangeTimer.Elapsed += (o, args) =>
                    {
                        if (_currentBannerIndex + 1 > _headlines.Banner.Length - 1)
                        {
                            _currentBannerIndex = 0;
                        }
                        else
                        {
                            _currentBannerIndex++;
                        }

                        this.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            BannerImage.Source = _bannerBitmaps[_currentBannerIndex];
                        }));

                        _bannerChangeTimer.Start();
                    };

                    _bannerChangeTimer.Start();

                    NewsListView.ItemsSource = _headlines.News;
                }
                catch (Exception)
                {
                    NewsListView.Items.Add(new News()
                    {
                        Title = "Could not download news data.",
                        Tag = "DlError"
                    });
                }
            });

            setupHeadlines();

            Settings.LanguageChanged += setupHeadlines;

            this.Visibility = Visibility.Visible;

            try
            {
                Task.Run(() => StartServiceAddon());
            }
            catch (Exception exc)
            {
                new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Addons").ShowDialog();
                isLoggingIn = false;
            }

            var version = Util.GetAssemblyVersion();
            if (Properties.Settings.Default.LastVersion != version)
            {
                MessageBox.Show($"XIVLauncher was updated to version {version}. This release includes:\n\nPolishing up UI for in-game features - they are not a normal addon anymore and can be enabled in the in-game tab itself\nAdditionally, in-game features now have a step in first-time setup.\n\nAuto-login changes - Auto-login can now only be disabled by holding SHIFT when starting the game. Thanks to Genesis!\n\nA Discord link was added to the about tab, and a new error window style that links to the FAQ was added.", "XIVLauncher updated!", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                Properties.Settings.Default.LastVersion = version;
                Properties.Settings.Default.Save();
            }

            BringIntoView();
        }

        private void AutoUpdaterOnCheckForUpdateEvent(UpdateInfoEventArgs args)
        {
            if (args != null)
            {
                if (args.IsUpdateAvailable)
                {
                    try
                    {
                        MessageBox.Show("An update for XIVLauncher is available. It will now be downloaded, the application will restart.",
                            "XIVLauncher Update", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                        if (AutoUpdater.DownloadUpdate())
                        {
                            Environment.Exit(0);
                        }
                        else
                        {
                            Util.ShowError($"Could not download update. Please try again later.", "Update failed");
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception exc)
                    {
                        Util.ShowError($"Update failed. Please report this error and try again later. \n\n{exc}", "Update failed");
                        Environment.Exit(0);
                    }
                }
                else
                {
                    InitializeWindow();
                }
            }
            else
            {
                Util.ShowError($"Could not check for updates. Please try again later.", "Update failed");
                Environment.Exit(0);
            }
        }

        private void OtpTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            /*
            LoadingMessageCancelButton.Visibility = Visibility.Hidden;
            LoadingMessageTextBlock.Text = "Logging in...";

            DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);
            Task.Run(() => { this.Dispatcher.BeginInvoke(new Action(() => {  })); });
            */

            if (isLoggingIn)
                return;

            HandleLogin(false);
            isLoggingIn = true;
        }

        internal void HandleLogin(bool autoLogin, string onetimePassword = null)
        {
            OtpTextBox.Text = onetimePassword ?? "";

            var hasValidCache = _game.Cache.HasValidCache(LoginUsername.Text) && Settings.IsUniqueIdCacheEnabled();

            if (OtpCheckBox.IsChecked == true && !hasValidCache)
            {
                DialogHost.OpenDialogCommand.Execute(null, OtpDialogHost);
            }

            if (SaveLoginCheckBox.IsChecked == true)
            {
                Settings.SaveCredentials(AppName, LoginUsername.Text, LoginPassword.Password);
                Settings.SetNeedsOtp(OtpCheckBox.IsChecked == true);

                if (!autoLogin)
                {
                    if (AutoLoginCheckBox.IsChecked == true)
                    {
                        var result = MessageBox.Show("This option will log you in automatically with the credentials you entered.\nTo reset it again, launch this application while holding the Shift key.\n\nDo you really want to enable it?", "Enabling Autologin", MessageBoxButton.YesNo);

                        if (result == MessageBoxResult.No)
                        {
                            AutoLoginCheckBox.IsChecked = false;
                        }
                    }
                    else
                    {
                        AutoLoginCheckBox.IsChecked = false;
                    }

                    Settings.SetAutologin(AutoLoginCheckBox.IsChecked == true);
                }

                Settings.Save();
            }
            else
            {
                Settings.ResetCredentials(AppName);
                Settings.Save();
            }

            if (OtpCheckBox.IsChecked == false || hasValidCache)
            {
                StartGame();
            }
        }

        private void StartAddons(Process gameProcess)
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.GameLaunched))
            {
                addonEntry.Addon.Run(gameProcess);
            }
        }

        private void StartServiceAddon()
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.LauncherInitialised))
            {
                if (addonEntry.Addon is IServiceAddon)
                    (addonEntry.Addon as IServiceAddon).Run(this);
            }
        }

        private void StopServiceAddons()
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.LauncherInitialised))
            {
                if (addonEntry.Addon is IServiceAddon)
                    (addonEntry.Addon as IServiceAddon).Stop();
            }
        }

        private async void StartGame()
        {
            try
            {
                var gateStatus = false;
                try
                {
                    gateStatus = await Task.Run(() => _game.GetGateStatus());
                }
                catch
                {
                    // ignored
                }

                if (!gateStatus)
                {
                    MessageBox.Show(
                        "Square Enix seems to be running maintenance work right now or the login server is unreachable. The game shouldn't be launched.", "Error", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                    return;
                }

                //LoadingMessageCancelButton.Visibility = Visibility.Hidden;
                //LoadingMessageTextBlock.Text = "Logging in...";

                //DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);

                var gameProcess = _game.Login(LoginUsername.Text, LoginPassword.Password, OtpTextBox.Text, Settings.IsUniqueIdCacheEnabled());

                if (gameProcess == null)
                    return;

                try
                {
                    await Task.Run(() => StartAddons(gameProcess));
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Addons").ShowDialog();
                    isLoggingIn = false;
                }

                try
                {
                    if (Settings.IsInGameAddonEnabled())
                    {
                        await Task.Run(() =>
                        {
                            new HooksAddon().Run(gameProcess);
                        });
                    }
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Hooks").ShowDialog();
                    isLoggingIn = false;
                }

                Environment.Exit(0);
            }
            catch (Exception exc)
            {
                new ErrorWindow(exc, "Additionally, please check your login information or try again.", "Login").ShowDialog();
                isLoggingIn = false;
            }
        }

        private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines != null)
            {
                Process.Start(_headlines.Banner[_currentBannerIndex].Link.ToString());
            }
        }

        private void SaveLoginCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsEnabled = true;
        }

        private void SaveLoginCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsChecked = false;
            AutoLoginCheckBox.IsEnabled = false;
        }

        private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines == null)
                return;

            if (NewsListView.SelectedItem is News item)
            {
                if (item.Url != string.Empty)
                {
                    Process.Start(item.Url);
                }
                else
                {
                    string url;
                    switch (Settings.GetLanguage())
                    {
                        case ClientLanguage.Japanese:

                            url = "https://jp.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.English:

                            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.German:

                            url = "https://de.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.French:

                            url = "https://fr.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        default:

                            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                            break;
                    }

                    Process.Start(url + item.Id);
                }
            }
        }

        private void WorldStatusButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("http://is.xivup.com/");
        }

        private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            new SettingsWindow().ShowDialog();
        }

        private void QueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            _maintenanceQueueTimer = new System.Timers.Timer
            {
                Interval = 5000
            };

            _maintenanceQueueTimer.Elapsed += (o, args) =>
            {
                var gateStatus = false;
                try
                {
                    gateStatus = _game.GetGateStatus();
                }
                catch
                {
                    // ignored
                }

                if (gateStatus)
                {
                    Console.Beep(529, 130);
                    System.Threading.Thread.Sleep(200);
                    Console.Beep(529, 100);
                    System.Threading.Thread.Sleep(30);
                    Console.Beep(529, 100);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(420, 140);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(466, 100);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(529, 160);
                    System.Threading.Thread.Sleep(200);
                    Console.Beep(466, 100);
                    System.Threading.Thread.Sleep(30);
                    Console.Beep(529, 900);

                    this.Dispatcher.BeginInvoke(new Action(() => LoginButton_Click(null, null)));
                    _maintenanceQueueTimer.Stop();
                    return;
                }

                _maintenanceQueueTimer.Start();
            };

            DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);
            _maintenanceQueueTimer.Start();
        }

        private void QuitMaintenanceQueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            _maintenanceQueueTimer.Stop();
            DialogHost.CloseDialogCommand.Execute(null, MaintenanceQueueDialogHost);
        }

        private void OtpTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                DialogHost.CloseDialogCommand.Execute(null, OtpDialogHost);
                StartGame();
            }
        }

        private void OtpTextBox_Click(object sender, RoutedEventArgs e)
        {
            DialogHost.CloseDialogCommand.Execute(null, OtpDialogHost);
            StartGame();
        }

        private void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return || isLoggingIn)
                return;

            HandleLogin(false);
            isLoggingIn = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            StopServiceAddons();
        }
    }
}
