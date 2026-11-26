// Checks for the game scan's filter and its error isolation: tools and launchers are never added as games, whichever
// pass finds their folder, and one manifest that can't be read skips only that game. Each check that needs folders
// makes them in a temporary folder of its own (TempDir, in GuardTests.cs); no path here has a backslash in it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- game scan ----

        public static void ScanFilterRejectsToolsAndLaunchers()
        {
            Run.True(GameScanner.NotAGame("wallpaper_engine", "wallpaper_engine", "common"), "Wallpaper Engine's Steam folder");
            Run.True(GameScanner.NotAGame("Steam: Wallpaper Engine", "wallpaper_engine", "common"), "Wallpaper Engine by its Steam name");
            Run.True(GameScanner.NotAGame("Steamworks Shared", "Steamworks Shared", "common"), "Steamworks Shared");
            Run.True(GameScanner.NotAGame("SteamLinuxRuntime_sniper", "SteamLinuxRuntime_sniper", "common"), "Steam Linux Runtime's folder");
            Run.True(GameScanner.NotAGame("UE_5.4", "UE_5.4", "Epic Games"), "an Unreal Engine install");
            Run.True(GameScanner.NotAGame("Riot Client", "Riot Client", "Riot Games"), "Riot Client");
            Run.True(GameScanner.NotAGame("Social Club", "Social Club", "Rockstar Games"), "Rockstar's Social Club");
            Run.True(GameScanner.NotAGame("Launcher", "Launcher", "Rockstar Games"), "Rockstar's launcher");
            Run.True(GameScanner.NotAGame("Launcher", "Launcher", "Epic Games"), "Epic's launcher");
            Run.True(GameScanner.NotAGame("Xbox: Riot Client", "Content", "Riot Client"), "a launcher by its label");
        }

        public static void ScanFilterKeepsGames()
        {
            foreach (string game in new[] { "Counter-Strike Global Offensive", "Cyberpunk 2077", "VALORANT", "League of Legends", "GTAV",
                "Red Dead Redemption 2", "Grand Theft Auto V", "Riot Games", "Launcher Of Doom" })
                Run.True(!GameScanner.NotAGame(game, game, "common"), game + " is a game");
            Run.True(!GameScanner.NotAGame("Steam: Counter-Strike 2", "Counter-Strike Global Offensive", "common"), "a Steam game by its name");
            Run.True(!GameScanner.NotAGame("Launcher", "Launcher", "Games"), "a folder called Launcher elsewhere is left to the scan");
            Run.True(!GameScanner.NotAGame(null, null, null), "nothing to go by");
        }

        /// <summary>Games in folders made under dir; null for a folder kept out as a tool or launcher.</summary>
        static string LabelIn(Dictionary<string, string> folders, string dir, string name)
        {
            string label;
            return folders.TryGetValue(Path.GetFullPath(Path.Combine(dir, name)), out label) ? label : "(not seen)";
        }

        public static void ScanNeverAddsSteamToolsByFolderName()
        {
            string dir = TempDir();
            try
            {
                string apps = Path.Combine(dir, "steamapps"), common = Path.Combine(apps, "common");
                foreach (string f in new[] { "wallpaper_engine", "Steamworks Shared", "Counter-Strike Global Offensive" })
                    Directory.CreateDirectory(Path.Combine(common, f));
                File.WriteAllText(Path.Combine(apps, "appmanifest_431960.acf"), "\"AppState\"\n{\n\t\"name\"\t\t\"Wallpaper Engine\"\n\t\"installdir\"\t\t\"wallpaper_engine\"\n}\n");
                File.WriteAllText(Path.Combine(apps, "appmanifest_730.acf"), "\"AppState\"\n{\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n}\n");

                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                GameScanner.SteamLibraries(new[] { apps }, folders);
                GameScanner.Subfolders(common, folders);   // the drives' pass, which goes by folder names

                Run.Equal<string>(null, LabelIn(folders, common, "wallpaper_engine"), "Wallpaper Engine is kept out");
                Run.Equal<string>(null, LabelIn(folders, common, "Steamworks Shared"), "Steamworks Shared is kept out");
                Run.Equal("Steam: Counter-Strike 2", LabelIn(folders, common, "Counter-Strike Global Offensive"), "the game keeps Steam's name");
                Run.True(!GameScanner.AddFolder(folders, Path.Combine(common, "wallpaper_engine"), "Some Game"), "a folder kept out stays out under another name");
                Run.Equal<string>(null, LabelIn(folders, common, "wallpaper_engine"), "… and keeps no label");
            }
            finally { Clean(dir); }
        }

        public static void ScanSkipsLauncherFoldersNextToGames()
        {
            string dir = TempDir();
            try
            {
                string riot = Path.Combine(dir, "Riot Games"), rockstar = Path.Combine(dir, "Program Files", "Rockstar Games");
                foreach (string f in new[] { "Riot Client", "VALORANT", "League of Legends" }) Directory.CreateDirectory(Path.Combine(riot, f));
                foreach (string f in new[] { "Launcher", "Social Club", "GTAV", "Red Dead Redemption 2" }) Directory.CreateDirectory(Path.Combine(rockstar, f));

                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                GameScanner.Subfolders(riot, folders);
                GameScanner.Subfolders(rockstar, folders);

                foreach (string f in new[] { "Riot Client" }) Run.Equal<string>(null, LabelIn(folders, riot, f), f + " is kept out");
                foreach (string f in new[] { "Launcher", "Social Club" }) Run.Equal<string>(null, LabelIn(folders, rockstar, f), f + " is kept out");
                foreach (string f in new[] { "VALORANT", "League of Legends" }) Run.Equal(f, LabelIn(folders, riot, f), f + " is found");
                foreach (string f in new[] { "GTAV", "Red Dead Redemption 2" }) Run.Equal(f, LabelIn(folders, rockstar, f), f + " is found");
            }
            finally { Clean(dir); }
        }

        public static void ScanSkipsOnlyTheBrokenManifest()
        {
            string dir = TempDir();
            try
            {
                string apps = Path.Combine(dir, "steamapps"), common = Path.Combine(apps, "common");
                foreach (string f in new[] { "Locked", "Portal 2", "Cyberpunk 2077" }) Directory.CreateDirectory(Path.Combine(common, f));
                // 1: an install folder no path can have (a NUL); 2: locked by another program; 3 and 4: fine.
                File.WriteAllText(Path.Combine(apps, "appmanifest_1.acf"), "\"name\" \"Broken\"\n\"installdir\" \"bad\0dir\"\n");
                File.WriteAllText(Path.Combine(apps, "appmanifest_2.acf"), "\"name\" \"Locked\"\n\"installdir\" \"Locked\"\n");
                File.WriteAllText(Path.Combine(apps, "appmanifest_3.acf"), "\"name\" \"Portal 2\"\n\"installdir\" \"Portal 2\"\n");
                File.WriteAllText(Path.Combine(apps, "appmanifest_4.acf"), "\"name\" \"Cyberpunk 2077\"\n\"installdir\" \"Cyberpunk 2077\"\n");

                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (new FileStream(Path.Combine(apps, "appmanifest_2.acf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    GameScanner.SteamLibraries(new[] { Path.Combine(dir, "no such library"), apps }, folders);

                Run.Equal("Steam: Portal 2", LabelIn(folders, common, "Portal 2"), "a game after the broken manifests is found");
                Run.Equal("Steam: Cyberpunk 2077", LabelIn(folders, common, "Cyberpunk 2077"), "the next one too");
                Run.True(LogText().Contains("appmanifest_1.acf"), "the broken manifest is logged");
            }
            finally { Clean(dir); }
        }

        public static void ScanSkipsSharedEmulators()
        {
            // GOG runs each DOS / adventure classic through its own copy of DOSBox or ScummVM: one process name for all.
            foreach (string exe in new[] { "dosbox", "DOSBox", "dosbox-x", "scummvm" })
                Run.True(GameScanner.SkippedExe(exe), exe + " is not taken as a game");
            foreach (string exe in new[] { "Cyberpunk2077", "cs2", "GTA5", "RDR2", "VALORANT" })
                Run.True(!GameScanner.SkippedExe(exe), exe + " is a game");
        }
    }
}
