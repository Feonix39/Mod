using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdysTech.CredentialManager;
using AutoUpdaterDotNET;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Addon;
using XIVLauncher.Cache;
using XIVLauncher.Game;
using Timer = System.Timers.Timer;

namespace XIVLauncher.Windows
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Timer _bannerChangeTimer;
        private Headlines _headlines;
        private BitmapImage[] _bannerBitmaps;
        private int _currentBannerIndex;

        private Timer _maintenanceQueueTimer;

        private static string AppName = "FINAL FANTASY XIV";

        private readonly XivGame _game = new XivGame();

        private bool _isLoggingIn;

        public MainWindow(string accountName)
        {
            InitializeComponent();

            Title += " v" + Util.GetAssemblyVersion();

            if (!string.IsNullOrEmpty(accountName))
            {
                Title += " - Account: " + accountName;
                AppName += "-" + accountName;
            }

#if !DEBUG
            AutoUpdater.ShowSkipButton = false;
            AutoUpdater.ShowRemindLaterButton = false;
            AutoUpdater.Mandatory = true;
            AutoUpdater.UpdateMode = Mode.Forced;

            AutoUpdater.CheckForUpdateEvent += AutoUpdaterOnCheckForUpdateEvent;

            Log.Information("Starting update check.");
            AutoUpdater.Start("https://goaaats.github.io/ffxiv/tools/launcher/update.xml");
#else
            InitializeWindow();
#endif
        }

        private void SetupHeadlines()
        {
            try
            {
                _bannerChangeTimer?.Stop();

                _headlines = Headlines.Get(_game);

                _bannerBitmaps = new BitmapImage[_headlines.Banner.Length];
                for (var i = 0; i < _headlines.Banner.Length; i++)
                {
                    var imageBytes = _game.DownloadAsLauncher(_headlines.Banner[i].LsbBanner.ToString());

                    using (var stream = new MemoryStream(imageBytes))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = stream;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();

                        _bannerBitmaps[i] = bitmapImage;
                    }
                }

                Dispatcher.BeginInvoke(new Action(() => { BannerImage.Source = _bannerBitmaps[0]; }));

                _bannerChangeTimer = new Timer {Interval = 5000};

                _bannerChangeTimer.Elapsed += (o, args) =>
                {
                    if (_currentBannerIndex + 1 > _headlines.Banner.Length - 1)
                        _currentBannerIndex = 0;
                    else
                        _currentBannerIndex++;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BannerImage.Source = _bannerBitmaps[_currentBannerIndex];
                    }));
                };

                _bannerChangeTimer.AutoReset = true;
                _bannerChangeTimer.Start();

                Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News; }));
            }
            catch (Exception)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NewsListView.Items.Add(new News {Title = "Could not download news data.", Tag = "DlError"});
                }));
            }
        }

        private void InitializeWindow()
        {
            // Upgrade the stored settings if needed
            if (Properties.Settings.Default.UpgradeRequired)
            {
                Log.Information("Settings upgrade required...");
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

            if (!gateStatus) WorldStatusPackIcon.Foreground = new SolidColorBrush(Color.FromRgb(242, 24, 24));

            var savedCredentials = CredentialManager.GetCredentials(AppName);

            if (savedCredentials != null)
            {
                LoginUsername.Text = savedCredentials.UserName;
                LoginPassword.Password = savedCredentials.Password;
                OtpCheckBox.IsChecked = Settings.NeedsOtp();
                AutoLoginCheckBox.IsChecked = Settings.IsAutologin();
                SaveLoginCheckBox.IsChecked = true;
            }

            if (Settings.IsAutologin() && savedCredentials != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                Log.Information("Engaging Autologin");

                try
                {
                    if (!gateStatus)
                    {
                        MessageBox.Show(
                            "Square Enix seems to be running maintenance work right now. The game shouldn't be launched.");
                        Settings.SetAutologin(false);
                        _isLoggingIn = false;
                    }
                    else
                    {
                        HandleLogin(true);
                        Settings.Save();
                        return;
                    }
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "Additionally, please check your login information or try again.", "AutoLogin")
                        .ShowDialog();
                    Settings.SetAutologin(false);
                    _isLoggingIn = false;
                }

                Settings.Save();
            }

            if (Settings.GamePath == null)
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();
            }

            Task.Run(() => SetupHeadlines());

            Settings.LanguageChanged += SetupHeadlines;

            var version = Util.GetAssemblyVersion();
            if (Properties.Settings.Default.LastVersion != version)
            {
                MessageBox.Show(
                    $"XIVLauncher was updated to version {version}. This release features some new features and fixes:\r\n\r\n* Rewrote the addon system - if you had issues running Rich Presence or In-Game addon before, this should work better. (XIVLauncher.exe will now stay in the background while the game is running - this is intended behavior)\r\n* Added an option \"Kill when game is closing\" to the generic addon setup screen. This will kill any EXE you are launching with XIVLauncher after the game closes. (suggested by @omgitsnewton)\r\n* Added \"/xlmute\", \"/xlmutelist\", \"/xlunmute\" commands to In-Game addon. These will let you mute any specific words or sentences. (suggested by @omgitsnewton)\r\n* Update the In-Game addon RMT block list.\r\n* Updated both In-Game addon and Rich Presence, and made them easier to maintain so updates should happen way quicker in the future.\r\n* Added an option to add custom game launch arguments in the Settings->Game tab. (suggested by @Biscuit)\r\n* The OTP shortcut HTTP server will now reply correctly to requests, so they can terminate. (Thanks to @Roy)\r\n* Rewrote the UID cache system.\r\n* Fixed a bug wherein the DirectX 9 checkbox would not appear checked in the settings window when DirectX 9 was selected.\r\n* Fixed a bug wherein the OTP shortcut HTTP server would not close correctly and crash XIVLauncher when trying to log in a second time.",
                    "XIVLauncher updated!", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                Properties.Settings.Default.LastVersion = version;
                Settings.UniqueIdCache = new List<UniqueIdCacheEntry>();
                Properties.Settings.Default.Save();
            }

            Show();
            Activate();

            Log.Information("MainWindow initialized.");
        }

        private void AutoUpdaterOnCheckForUpdateEvent(UpdateInfoEventArgs args)
        {
            Log.Information("AutoUpdaterOnCheckForUpdateEvent called.");
            if (args != null)
            {
                if (args.IsUpdateAvailable)
                {
                    try
                    {
                        Log.Information("Update available, trying to download.");
                        MessageBox.Show(
                            "An update for XIVLauncher is available. It will now be downloaded, the application will restart.",
                            "XIVLauncher Update", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                        if (AutoUpdater.DownloadUpdate())
                        {
                            Environment.Exit(0);
                        }
                        else
                        {
                            Util.ShowError("Could not download update. Please try again later.", "Update failed");
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception exc)
                    {
                        new ErrorWindow(exc, $"Update failed. Please report this error and try again later. \n\n{exc}",
                            "UpdateAvailableFail").ShowDialog();
                        Environment.Exit(0);
                    }
                }
                else
                {
                    Log.Information("No update: {0}", args.CurrentVersion);
                    InitializeWindow();
                }
            }
            else
            {
                Util.ShowError("Could not check for updates. Please try again later.", "Update failed");
                Log.Error("Update check failed.");
                Environment.Exit(0);
            }
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoggingIn)
                return;

            _isLoggingIn = true;
            HandleLogin(false);
        }


        #region Login

        private void HandleLogin(bool autoLogin)
        {
            var hasValidCache = _game.Cache.HasValidCache(LoginUsername.Text) && Settings.UniqueIdCacheEnabled;

            if (SaveLoginCheckBox.IsChecked == true)
            {
                Settings.SaveCredentials(AppName, LoginUsername.Text, LoginPassword.Password);
                Settings.SetNeedsOtp(OtpCheckBox.IsChecked == true);

                if (!autoLogin)
                {
                    if (AutoLoginCheckBox.IsChecked == true)
                    {
                        var result = MessageBox.Show(
                            "This option will log you in automatically with the credentials you entered.\nTo reset it again, launch this application while holding the Shift key.\n\nDo you really want to enable it?",
                            "Enabling Autologin", MessageBoxButton.YesNo);

                        if (result == MessageBoxResult.No) AutoLoginCheckBox.IsChecked = false;
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

            var otp = "";
            if (OtpCheckBox.IsChecked == true && !hasValidCache)
            {
                var otpDialog = new OtpInputDialog();
                otpDialog.ShowDialog();

                if (otpDialog.Result == null)
                {
                    _isLoggingIn = false;

                    if (autoLogin)
                        Environment.Exit(0);

                    return;
                }

                otp = otpDialog.Result;
            }

            StartGame(otp);
        }

        private async void StartGame(string otp)
        {
            Log.Information("StartGame() called");
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
                    Log.Information("GateStatus is false.");
                    MessageBox.Show(
                        "Square Enix seems to be running maintenance work right now or the login server is unreachable. The game shouldn't be launched.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                    _isLoggingIn = false;

                    return;
                }

                var gameProcess = _game.Login(LoginUsername.Text, LoginPassword.Password, otp,
                    Settings.SteamIntegrationEnabled, Settings.AdditionalLaunchArgs, Settings.UniqueIdCacheEnabled);

                if (gameProcess == null)
                {
                    Log.Error("GameProcess was null...");
                    _isLoggingIn = false;
                    return;
                }

                var addonMgr = new AddonManager();

                try
                {
                    await Task.Run(() => addonMgr.RunAddons(gameProcess, Settings.GetAddonList().Where(x => x.IsEnabled).ToList()));
                }
                catch (Exception ex)
                {
                    new ErrorWindow(ex,
                        "This could be caused by your antivirus, please check its logs and add any needed exclusions.",
                        "Addons").ShowDialog();
                    _isLoggingIn = false;

                    addonMgr.StopAddons();
                }

                try
                {
                    if (Settings.IsInGameAddonEnabled())
                        await Task.Run(() =>
                        {
                            var hooks = new HooksAddon(); hooks.Setup(gameProcess); hooks.Run(); });
                }
                catch (Exception ex)
                {
                    new ErrorWindow(ex,
                        "This could be caused by your antivirus, please check its logs and add any needed exclusions.",
                        "Hooks").ShowDialog();
                    _isLoggingIn = false;

                    addonMgr.StopAddons();
                }

                this.Close();
                
                var watchThread = new Thread(() =>
                {
                    while (!gameProcess.HasExited)
                    {
                        gameProcess.Refresh();
                        Thread.Sleep(1);
                    }

                    Log.Information("Game has exited.");
                    addonMgr.StopAddons();
                    Environment.Exit(0);
                });
                watchThread.Start();
            }
            catch (Exception ex)
            {
                new ErrorWindow(ex, "Please also check your login information or try again.", "Login").ShowDialog();
                _isLoggingIn = false;
            }
        }

        #endregion

        private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines != null) Process.Start(_headlines.Banner[_currentBannerIndex].Link.ToString());
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
            _maintenanceQueueTimer = new Timer
            {
                Interval = 5000
            };

            _maintenanceQueueTimer.Elapsed += (o, args) =>
            {
                bool gateStatus;
                try
                {
                    gateStatus = _game.GetGateStatus();
                }
                catch
                {
                    // If getting our gate status fails, we shouldn't even bother
                    return;
                }

                if (gateStatus)
                {
                    Console.Beep(529, 130);
                    Thread.Sleep(200);
                    Console.Beep(529, 100);
                    Thread.Sleep(30);
                    Console.Beep(529, 100);
                    Thread.Sleep(300);
                    Console.Beep(420, 140);
                    Thread.Sleep(300);
                    Console.Beep(466, 100);
                    Thread.Sleep(300);
                    Console.Beep(529, 160);
                    Thread.Sleep(200);
                    Console.Beep(466, 100);
                    Thread.Sleep(30);
                    Console.Beep(529, 900);

                    Dispatcher.BeginInvoke(new Action(() => LoginButton_Click(null, null)));
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

        private void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return || _isLoggingIn)
                return;

            HandleLogin(false);
            _isLoggingIn = true;
        }

        private void MainWindow_OnClosed(object sender, EventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}