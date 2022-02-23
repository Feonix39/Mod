﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using CheapLoc;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel
{
    class ErrorWindowViewModel
    {
        public ICommand CopyMessageTextCommand { get; set; }

        public ErrorWindowViewModel()
        {
            SetupLoc();
        }

        private void SetupLoc()
        {
            ErrorExplanationMsgLoc = Loc.Localize("ErrorExplanation",
                "An error in XIVLauncher occurred. Please consult the FAQ. If this issue persists, please report\r\nit on GitHub by clicking the button below, describing the issue and copying the text in the box.");
            OfficialLauncherLoc = Loc.Localize("StartOfficialLauncher", "Official Launcher");
            JoinDiscordLoc = Loc.Localize("JoinDiscord", "Join Discord");
            OpenIntegrityReportLoc = Loc.Localize("OpenIntegrityReport", "Open Integrity Report");
            OpenFaqLoc = Loc.Localize("OpenFaq", "Open FAQ");
            ReportErrorLoc = Loc.Localize("ReportError", "Report error");
            OkLoc = Loc.Localize("OK", "OK");
            YesWithShortcutLoc = Loc.Localize("Yes", "_Yes");
            NoWithShortcutLoc = Loc.Localize("No", "_No");
            CancelWithShortcutLoc = Loc.Localize("Cancel", "_Cancel");
            CopyWithShortcutLoc = Loc.Localize("Copy", "_Copy");
        }

        public string ErrorExplanationMsgLoc { get; private set; }
        public string OfficialLauncherLoc { get; private set; }
        public string JoinDiscordLoc { get; private set; }
        public string OpenIntegrityReportLoc { get; private set; }
        public string OpenFaqLoc { get; private set; }
        public string ReportErrorLoc { get; private set; }
        public string OkLoc { get; private set; }
        public string YesWithShortcutLoc { get; private set; }
        public string NoWithShortcutLoc { get; private set; }
        public string CancelWithShortcutLoc { get; private set; }
        public string CopyWithShortcutLoc { get; private set; }
    }
}