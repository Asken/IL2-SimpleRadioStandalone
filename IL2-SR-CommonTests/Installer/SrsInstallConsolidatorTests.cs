using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Tests.Installer
{
    [TestClass]
    public class SrsInstallConsolidatorTests
    {
        [TestMethod]
        public void MigrationPreservesProfilesConflictsFavouritesAndBackups()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-consolidation-" + Guid.NewGuid().ToString("N"));
            string sourceA = Path.Combine(root, "sourceA");
            string sourceB = Path.Combine(root, "sourceB");
            string destination = Path.Combine(root, "destination");
            string userData = Path.Combine(root, "userdata");

            try
            {
                Directory.CreateDirectory(sourceA);
                Directory.CreateDirectory(sourceB);
                Directory.CreateDirectory(destination);

                Write(Path.Combine(sourceA, "IL2-SR-ClientRadio.exe"), "dummy");
                Write(Path.Combine(sourceB, "IL2-SR-ClientRadio.exe"), "dummy");
                Write(Path.Combine(sourceA, "global.cfg"),
                    "[Client Settings]\r\nSettingsProfiles={default,pilot}\r\nTheme=A");
                Write(Path.Combine(sourceA, "default.cfg"), "A-default");
                Write(Path.Combine(sourceA, "pilot.cfg"), "A-pilot-bindings");
                Write(Path.Combine(sourceA, "FavouriteServers.csv"), "A,a.example:6002,True\r\n");

                Write(Path.Combine(sourceB, "global.cfg"),
                    "[Client Settings]\r\nSettingsProfiles={default,korea}\r\nTheme=B");
                Write(Path.Combine(sourceB, "default.cfg"), "B-default");
                Write(Path.Combine(sourceB, "korea.cfg"), "B-korea-bindings");
                Write(Path.Combine(sourceB, "FavouriteServers.csv"), "B,b.example:7002,False\r\n");

                var plan = new global::Installer.SrsConsolidationPlan(
                    destination,
                    new[] { sourceA, sourceB });

                global::Installer.SrsConsolidationResult result =
                    global::Installer.SrsInstallConsolidator.MigrateUserDataTo(
                        plan,
                        sourceA,
                        userData,
                        null);

                string globalConfig = File.ReadAllText(Path.Combine(userData, "global.cfg"));
                StringAssert.Contains(globalConfig, "Theme=A");
                StringAssert.Contains(globalConfig, "default");
                StringAssert.Contains(globalConfig, "pilot");
                StringAssert.Contains(globalConfig, "korea");
                StringAssert.Contains(globalConfig, "default-from-sourceB");

                Assert.AreEqual("A-pilot-bindings", File.ReadAllText(Path.Combine(userData, "pilot.cfg")));
                Assert.AreEqual("B-korea-bindings", File.ReadAllText(Path.Combine(userData, "korea.cfg")));
                Assert.AreEqual("B-default", File.ReadAllText(Path.Combine(userData, "default-from-sourceB.cfg")));
                Assert.AreEqual(2, File.ReadAllLines(Path.Combine(userData, "FavouriteServers.csv")).Length);
                Assert.IsTrue(Directory.Exists(result.BackupPath));
                Assert.IsTrue(File.Exists(Path.Combine(userData, ".legacy-migration-complete")));

                int retired = global::Installer.SrsInstallConsolidator.RetireDuplicateInstallations(plan, null);
                Assert.AreEqual(2, retired);
                Assert.IsFalse(File.Exists(Path.Combine(sourceA, "IL2-SR-ClientRadio.exe")));
                Assert.IsFalse(File.Exists(Path.Combine(sourceB, "IL2-SR-ClientRadio.exe")));
                Assert.IsTrue(File.Exists(Path.Combine(sourceA, "global.cfg")));
                Assert.IsTrue(File.Exists(Path.Combine(sourceB, "global.cfg")));

                var subsequentUpdate = new global::Installer.SrsConsolidationPlan(
                    destination,
                    new string[0]);
                global::Installer.SrsConsolidationResult subsequentResult =
                    global::Installer.SrsInstallConsolidator.MigrateUserDataTo(
                        subsequentUpdate,
                        destination,
                        userData,
                        null);
                Assert.IsNull(subsequentResult.BackupPath);
                Assert.AreEqual(1, File.ReadAllText(Path.Combine(userData, "global.cfg"))
                    .Split(new[] { "default-from-sourceB" }, StringSplitOptions.None).Length - 1);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void CompletedMigrationStillRecoversWhitelistFromCurrentInstallFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-whitelist-recovery-" + Guid.NewGuid().ToString("N"));
            string installation = Path.Combine(root, "installation");
            string userData = Path.Combine(root, "userdata");

            try
            {
                Directory.CreateDirectory(installation);
                Directory.CreateDirectory(userData);
                Write(Path.Combine(userData, ".legacy-migration-complete"), "done");
                Write(Path.Combine(userData, "whitelist.txt"), "existing-guid\r\n");
                Write(Path.Combine(installation, "whitelist.txt"), "EXISTING-GUID\r\nnew-vpc-guid\r\n");

                var plan = new global::Installer.SrsConsolidationPlan(
                    installation,
                    new[] { installation });

                global::Installer.SrsConsolidationResult result =
                    global::Installer.SrsInstallConsolidator.MigrateUserDataTo(
                        plan,
                        installation,
                        userData,
                        null);

                CollectionAssert.AreEqual(
                    new[] { "existing-guid", "new-vpc-guid" },
                    File.ReadAllLines(Path.Combine(userData, "whitelist.txt")));
                Assert.AreEqual(1, result.ImportedLegacyDeviceListEntryCount);
                Assert.AreEqual(0, result.MigratedInstallationCount);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void DefaultInstallPathKeepsExistingCustomInstallFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-default-path-" + Guid.NewGuid().ToString("N"));
            string existing = Path.Combine(root, "Games", "SRS");
            string recommended = Path.Combine(root, "Program Files", "IL2-SimpleRadio-Standalone");

            try
            {
                Directory.CreateDirectory(existing);
                Write(Path.Combine(existing, "IL2-SR-ClientRadio.exe"), "dummy");

                Assert.AreEqual(existing, SelectDefaultInstallPath(existing + "\\", recommended));
                Assert.AreEqual(existing, SelectDefaultInstallPath("\"" + existing + "\"", recommended));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void DefaultInstallPathFallsBackToRecommendedWithoutUsableExistingInstall()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-default-path-" + Guid.NewGuid().ToString("N"));
            string emptyFolder = Path.Combine(root, "Games", "OldSRS");
            string recommended = Path.Combine(root, "Program Files", "IL2-SimpleRadio-Standalone");

            try
            {
                Directory.CreateDirectory(emptyFolder);

                Assert.AreEqual(recommended, SelectDefaultInstallPath(null, recommended));
                Assert.AreEqual(recommended, SelectDefaultInstallPath("", recommended));
                Assert.AreEqual(recommended, SelectDefaultInstallPath(Path.Combine(root, "Missing"), recommended));
                Assert.AreEqual(recommended, SelectDefaultInstallPath(emptyFolder, recommended));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void DefaultInstallPathIgnoresExistingInstallInsideGameFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-default-path-" + Guid.NewGuid().ToString("N"));
            string insideGame = Path.Combine(root, "IL-2 Sturmovik Great Battles", "SRS");
            string recommended = Path.Combine(root, "Program Files", "IL2-SimpleRadio-Standalone");

            try
            {
                Directory.CreateDirectory(insideGame);
                Write(Path.Combine(insideGame, "IL2-SR-ClientRadio.exe"), "dummy");

                Assert.AreEqual(recommended, global::Installer.SrsInstallConsolidator.SelectDefaultInstallPath(
                    insideGame,
                    recommended,
                    path => path.StartsWith(Path.Combine(root, "IL-2 Sturmovik Great Battles"), StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void CiribobVersionsAreTheOnesBelowThisForksFirstRelease()
        {
            Assert.IsTrue(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("1.0.2.0"));
            Assert.IsTrue(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("1.0.1.4"));
            Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("1.0.3.0"));
            Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("1.0.4.12"));
            Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("1.0.5.0"));
            Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion(null));
            Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobClientVersion("not a version"));
        }

        [TestMethod]
        public void UpgradeFromCiribobKeepsSettingsAndAddsClassicThemeAndEnglish()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-ciribob-" + Guid.NewGuid().ToString("N"));
            string oldInstall = Path.Combine(root, "old");
            string userData = Path.Combine(root, "userdata");

            try
            {
                Directory.CreateDirectory(oldInstall);
                Write(Path.Combine(oldInstall, "IL2-SR-ClientRadio.exe"), "dummy");
                Write(Path.Combine(oldInstall, "global.cfg"),
                    "[Position Settings]\r\nRadioX=100\r\n\r\n[Client Settings]\r\nSettingsProfiles={default.cfg}\r\nLastSeenName=Pilot\r\nSpeakerBoost=0.7");
                Write(Path.Combine(oldInstall, "default.cfg"), "[Client Settings]\r\nRadio1Channel=-0.3");
                Write(Path.Combine(oldInstall, "FavouriteServers.csv"), "Combat Box,srs.combatbox.net:6002,True\r\n");

                var plan = CiribobPlan(oldInstall, "1.0.2.0");
                Assert.IsTrue(global::Installer.SrsInstallConsolidator.IsCiribobUpgradeTo(plan, userData));

                global::Installer.SrsInstallConsolidator.MigrateUserDataTo(plan, oldInstall, userData, null);

                string globalConfig = File.ReadAllText(Path.Combine(userData, "global.cfg"));
                StringAssert.Contains(globalConfig, "Theme=Classic");
                StringAssert.Contains(globalConfig, "Language=en");
                StringAssert.Contains(globalConfig, "LastSeenName=Pilot");
                StringAssert.Contains(globalConfig, "SpeakerBoost=0.7");
                StringAssert.Contains(globalConfig, "RadioX=100");
                StringAssert.Contains(globalConfig, "SettingsProfiles={default}");
                Assert.AreEqual("[Client Settings]\r\nRadio1Channel=-0.3", File.ReadAllText(Path.Combine(userData, "default.cfg")));
                StringAssert.Contains(File.ReadAllText(Path.Combine(userData, "FavouriteServers.csv")), "srs.combatbox.net:6002");

                // Theme and language go in the [Client Settings] section, which SRS reads them from.
                string clientSection = globalConfig.Substring(globalConfig.IndexOf("[Client Settings]", StringComparison.Ordinal));
                StringAssert.Contains(clientSection, "Theme=Classic");
                StringAssert.Contains(clientSection, "Language=en");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void UpgradeDefaultsNeverReplaceSettingsTheUserAlreadyHas()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-ciribob-" + Guid.NewGuid().ToString("N"));
            string oldInstall = Path.Combine(root, "old");
            string userData = Path.Combine(root, "userdata");

            try
            {
                Directory.CreateDirectory(oldInstall);
                Write(Path.Combine(oldInstall, "IL2-SR-ClientRadio.exe"), "dummy");
                Write(Path.Combine(oldInstall, "global.cfg"),
                    "[Client Settings]\r\nSettingsProfiles={default}\r\nTheme=Grey\r\nLanguage=de");

                global::Installer.SrsInstallConsolidator.MigrateUserDataTo(
                    CiribobPlan(oldInstall, "1.0.2.0"), oldInstall, userData, null);

                string globalConfig = File.ReadAllText(Path.Combine(userData, "global.cfg"));
                StringAssert.Contains(globalConfig, "Theme=Grey");
                StringAssert.Contains(globalConfig, "Language=de");
                Assert.IsFalse(globalConfig.Contains("Classic"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void ForkInstallsAndExistingForkSettingsAreNotTreatedAsCiribobUpgrades()
        {
            string root = Path.Combine(Path.GetTempPath(), "il2-srs-ciribob-" + Guid.NewGuid().ToString("N"));
            string install = Path.Combine(root, "install");
            string userData = Path.Combine(root, "userdata");

            try
            {
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(userData);
                Write(Path.Combine(install, "IL2-SR-ClientRadio.exe"), "dummy");
                Write(Path.Combine(install, "global.cfg"), "[Client Settings]\r\nSettingsProfiles={default}");

                // A fork release in the old folder: no Classic default.
                var forkPlan = CiribobPlan(install, "1.0.4.7");
                Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobUpgradeTo(forkPlan, userData));
                global::Installer.SrsInstallConsolidator.MigrateUserDataTo(forkPlan, install, userData, null);
                Assert.IsFalse(File.ReadAllText(Path.Combine(userData, "global.cfg")).Contains("Theme="));

                // A leftover 1.0.2.0 folder when this fork's settings already exist: theirs are kept as they are.
                var ciribobPlan = CiribobPlan(install, "1.0.2.0");
                Assert.IsFalse(global::Installer.SrsInstallConsolidator.IsCiribobUpgradeTo(ciribobPlan, userData));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static global::Installer.SrsConsolidationPlan CiribobPlan(string install, string clientVersion)
        {
            return new global::Installer.SrsConsolidationPlan(install, new[] { install }, path => clientVersion);
        }

        private static string SelectDefaultInstallPath(string registeredPath, string recommendedPath)
        {
            return global::Installer.SrsInstallConsolidator.SelectDefaultInstallPath(
                registeredPath,
                recommendedPath,
                path => false);
        }

        private static void Write(string path, string value)
        {
            File.WriteAllText(path, value);
        }
    }
}
