namespace ScreenGuide.Skills.Windows.Tests;

public sealed class InstalledApplicationCatalogTests
{
    [Theory]
    [InlineData("网易云音乐", "桌面上的网易云")]
    [InlineData("抖音", "桌面的抖音")]
    [InlineData("Blender 5.2", "我桌面上的 Blender")]
    [InlineData("微信", "我的桌面上的微信客户端")]
    [InlineData("新星创作工具", "我的桌面的新星创作工具")]
    [InlineData("新星创作工具", "我桌面的新星创作工具")]
    [InlineData("新星创作工具", "电脑桌面上的新星创作工具")]
    [InlineData("新星创作工具", "我的电脑桌面上的新星创作工具")]
    [InlineData("新星创作工具", "我电脑桌面上的新星创作工具")]
    [InlineData("新星创作工具", "桌面上新星创作工具")]
    [InlineData("新星创作工具", "我见到桌面上的新星创作工具")]
    public void DesktopLocationQualifierResolvesTheRegisteredApplication(
        string registeredName, string spokenName)
    {
        var root = CreateRoot("desktop-qualifier");
        try
        {
            var registration = Registration(registeredName, root, "arbitrary-app.exe");
            var catalog = new InstalledApplicationCatalog(() => [registration]);

            Assert.Equal(registration.Target, catalog.ResolveByDisplayName(spokenName).LaunchTarget);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DesktopLocationQualifierDoesNotOverrideAnExactRegisteredName()
    {
        var root = CreateRoot("desktop-literal");
        try
        {
            var literal = Registration("桌面上的网易云", root, "literal.exe");
            var music = Registration("网易云音乐", root, "cloudmusic.exe");
            var catalog = new InstalledApplicationCatalog(() => [literal, music]);

            Assert.Equal(literal.Target, catalog.ResolveByDisplayName("桌面上的网易云").LaunchTarget);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("STEM")]
    [InlineData("我桌面上的STEM")]
    public void MisspelledNameCannotMatchInsideAnEnglishWord(string requested)
    {
        var root = CreateRoot("english-boundary");
        try
        {
            var steam = Registration("Steam", root, "steam.exe");
            var system = Registration("System Information", root, "msinfo32.exe");
            var catalog = new InstalledApplicationCatalog(() => [steam, system]);
            var error = Assert.Throws<InstalledApplicationResolutionException>(() => catalog.ResolveByDisplayName(requested));
            Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
            Assert.Contains("Steam", error.Message);
            Assert.DoesNotContain("System Information", error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("Code", "Visual Studio Code")]
    [InlineData("Blender", "Blender 5.2")]
    [InlineData("剪映", "剪映专业版")]
    public void CompleteWordsAndChineseNamesRemainUsable(string requested, string registered)
    {
        var root = CreateRoot("complete-word");
        try
        {
            var app = Registration(registered, root, "fixture.exe");
            var catalog = new InstalledApplicationCatalog(() => [app]);
            Assert.Equal(app.Target, catalog.ResolveByDisplayName(requested).LaunchTarget);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("斯蒂姆")]
    [InlineData("我见到桌面上的斯蒂姆")]
    public void SteamSpokenNameUsesTheRegisteredTarget(string requested)
    {
        var root = CreateRoot("spoken-steam");
        try
        {
            var app = Registration("Steam", root, "steam.exe");
            var catalog = new InstalledApplicationCatalog(() => [app]);
            Assert.Equal(app.Target, catalog.ResolveByDisplayName(requested).LaunchTarget);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExactRegisteredTypoNameWinsOverSimilaritySuggestions()
    {
        var root = CreateRoot("exact-before-suggestion");
        try
        {
            var literal = Registration("STEM", root, "literal.exe");
            var steam = Registration("Steam", root, "steam.exe");
            var catalog = new InstalledApplicationCatalog(() => [literal, steam]);
            Assert.Equal(literal.Target, catalog.ResolveByDisplayName("STEM").LaunchTarget);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SpokenAliasDoesNotHideMultipleRegisteredTargets()
    {
        var root = CreateRoot("spoken-ambiguity");
        try
        {
            var catalog = new InstalledApplicationCatalog(() =>
                [Registration("Steam", root, "first.exe"), Registration("Steam", root, "second.exe")]);
            var error = Assert.Throws<InstalledApplicationResolutionException>(() => catalog.ResolveByDisplayName("斯蒂姆"));
            Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("STEM", "System Information")]
    [InlineData("Stea", "Steam")]
    [InlineData("ox", "Firefox")]
    public void IncompleteEnglishWordsNeverSelectAnApplication(string requested, string registered)
    {
        var root = CreateRoot("partial-english");
        try
        {
            var app = Registration(registered, root, "fixture.exe");
            var catalog = new InstalledApplicationCatalog(() => [app]);
            var error = Assert.Throws<InstalledApplicationResolutionException>(() => catalog.ResolveByDisplayName(requested));
            Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DesktopLocationQualifierKeepsDifferentMatchingTargetsAmbiguous()
    {
        var root = CreateRoot("desktop-ambiguous");
        try
        {
            var catalog = new InstalledApplicationCatalog(() =>
            [
                Registration("新星创作工具", root, "first.exe"),
                Registration("新星创作工具", root, "second.exe")
            ]);

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName("我的桌面上的新星创作工具"));

            Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("抖音", "我的抖音软件")]
    [InlineData("Blender 5.2", "我的 Blender")]
    [InlineData("微信", "我电脑里的微信客户端")]
    [InlineData("新星创作工具", "我的新星创作工具")]
    public void NewlyRegisteredApplicationsUseGenericSpokenNamesWithoutAnAliasEntry(string registeredName, string spokenName)
    {
        var root = CreateRoot("generic-fresh");
        try
        {
            var registration = Registration(registeredName, root, "arbitrary-app.exe");
            var installed = false;
            var discoveries = 0;
            var catalog = new InstalledApplicationCatalog(() => {
                discoveries++;
                return installed ? [registration] : [];
            });
            _ = catalog.GetApplications();
            installed = true;
            Assert.Equal(registration.Target, catalog.ResolveByDisplayName(spokenName).LaunchTarget);
            Assert.Equal(2, discoveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("谷歌浏览器", "Google Chrome")]
    [InlineData("夸克", "夸克浏览器")]
    [InlineData("剪映", "剪映专业版")]
    [InlineData("剪映专业版", "剪映专业版")]
    [InlineData("网易云", "网易云音乐")]
    [InlineData("网易云音乐", "网易云音乐")]
    [InlineData("我的网易云", "网易云音乐")]
    [InlineData("我的网易云音乐", "网易云音乐")]
    [InlineData("我电脑上的网易云", "网易云音乐")]
    [InlineData("我电脑里的网易云音乐", "网易云音乐")]
    public void ResolveByDisplayName_UsesFixedSafeAliases(
        string requestedName,
        string expectedDisplayName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var registrations = new[]
        {
            Registration("Google Chrome", root, "chrome.exe"),
            Registration("夸克浏览器", root, "quark.exe"),
            Registration("剪映专业版", root, "jianying.exe"),
            Registration("网易云音乐", root, "cloudmusic.exe")
        };
        try
        {
            var catalog = new InstalledApplicationCatalog(() => registrations);

            var application = catalog.ResolveByDisplayName(requestedName);

            Assert.Equal(expectedDisplayName, application.DisplayName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_PrefersExactNameBeforePrefixOrContains()
    {
        var root = CreateRoot("precedence");
        try
        {
            var exact = Registration("示例", root, "exact.exe");
            var longer = Registration("示例专业版", root, "longer.exe");
            var catalog = new InstalledApplicationCatalog(() => [longer, exact]);

            var application = catalog.ResolveByDisplayName("示例");

            Assert.Equal(exact.Target, application.LaunchTarget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_MergesDuplicateRegistrationsOfSameCanonicalTarget()
    {
        var root = CreateRoot("duplicate");
        try
        {
            var target = Registration("示例应用", root, "same.exe");
            var catalog = new InstalledApplicationCatalog(
                () =>
                [
                    target,
                    target with
                    {
                        DisplayName = "示例应用专业版",
                        Target = Path.Combine(root, "示例应用专业版.lnk"),
                        Source = ApplicationRegistrationSource.Shortcut
                    }
                ],
                _ => new ShortcutResolution(target.Target));

            var exact = catalog.ResolveByDisplayName("示例应用");
            var alternate = catalog.ResolveByDisplayName("示例应用专业版");

            Assert.Equal(exact.Id, alternate.Id);
            Assert.Equal(target.Target, exact.LaunchTarget);
            Assert.Contains("示例应用专业版", exact.RegisteredNames!);
            Assert.Single(catalog.GetApplications(), item =>
                item.LaunchTarget == target.Target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_MergesDistinctTargetsWithSameTrustedApplicationIdentity()
    {
        var root = CreateRoot("trusted-identity");
        try
        {
            var platformTarget = CreateExecutable(root, "platform-notepad.exe");
            var registeredTarget = CreateExecutable(root, "registered-notepad.exe");
            var catalog = new InstalledApplicationCatalog(() =>
            [
                new ApplicationRegistration(
                    "记事本",
                    platformTarget,
                    ApplicationRegistrationSource.Platform,
                    PreferredId: "notepad"),
                new ApplicationRegistration(
                    "记事本",
                    registeredTarget,
                    ApplicationRegistrationSource.AppPath,
                    PreferredId: "notepad"),
                new ApplicationRegistration(
                    "记事本",
                    "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
                    ApplicationRegistrationSource.AppsFolder)
            ]);

            var application = catalog.ResolveByDisplayName("记事本");

            Assert.Equal("notepad", application.Id);
            Assert.Equal(platformTarget, application.LaunchTarget);
            Assert.Single(catalog.GetApplications(), item => item.Id == "notepad");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_RejectsAmbiguousDifferentTargets()
    {
        var root = CreateRoot("ambiguous");
        try
        {
            var catalog = new InstalledApplicationCatalog(() =>
            [
                Registration("同名工具", root, "first.exe"),
                Registration("同名工具", root, "second.exe")
            ]);

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName("同名工具"));

            Assert.Equal("application_ambiguous", error.Code);
            Assert.Contains("多个不同程序", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_RefreshesMissingDiscoveryAtMostOnce()
    {
        var root = CreateRoot("refresh");
        var discoveryCount = 0;
        try
        {
            var refreshed = Registration("刷新后应用", root, "refreshed.exe");
            var catalog = new InstalledApplicationCatalog(() =>
                ++discoveryCount == 1 ? [] : [refreshed]);

            var application = catalog.ResolveByDisplayName("刷新后应用");

            Assert.Equal(refreshed.Target, application.LaunchTarget);
            Assert.Equal(2, discoveryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplicationPreferenceMissUsesTheFreshDiscoveryWithoutImmediatelyRepeatingIt()
    {
        var root = CreateRoot("preference-fresh");
        try
        {
            var music = Registration("网易云音乐", root, "cloudmusic.exe");
            var discoveries = 0;
            var catalog = new InstalledApplicationCatalog(() =>
            {
                discoveries++;
                return [music];
            });

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveApplicationPreference("网易云，搜索周杰伦最新的歌"));

            Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
            Assert.Equal(music.Target, catalog.ResolveByDisplayName("网易云").LaunchTarget);
            Assert.Equal(1, discoveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplicationPreferenceDoesNotKeepAMissingWholeNameAcrossRequests()
    {
        var root = CreateRoot("preference-new-install");
        try
        {
            var chat = Registration("微信", root, "chat.exe");
            var literal = Registration("微信搜索助手", root, "assistant.exe");
            var installed = false;
            var discoveries = 0;
            var catalog = new InstalledApplicationCatalog(() =>
            {
                discoveries++;
                return installed ? [chat, literal] : [chat];
            });
            _ = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveApplicationPreference("微信搜索助手"));
            installed = true;

            var application = catalog.ResolveApplicationPreference("微信搜索助手");

            Assert.Equal(literal.Target, application.LaunchTarget);
            Assert.Equal(2, discoveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplicationPreferenceMissInAnExistingSnapshotRefreshesExactlyOnce()
    {
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            discoveries++;
            return [];
        });
        _ = catalog.GetApplications();

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            catalog.ResolveApplicationPreference("不存在的应用搜索助手"));

        Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
        Assert.Equal(2, discoveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationPreferenceAmbiguityCannotRefreshIntoAnotherTarget(bool prepopulateCache)
    {
        var root = CreateRoot("preference-ambiguous");
        try
        {
            var first = Registration("微信搜索助手", root, "first.exe");
            var second = Registration("微信搜索助手", root, "second.exe");
            var discoveries = 0;
            var catalog = new InstalledApplicationCatalog(() =>
                ++discoveries == 1 ? [first, second] : [first]);
            if (prepopulateCache) _ = catalog.GetApplications();

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveApplicationPreference("微信搜索助手"));

            Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
            Assert.Equal(1, discoveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("账户设置搜索助手")]
    [InlineData("以管理员身份运行微信")]
    [InlineData("C:\\tools\\unsafe.exe")]
    public void ApplicationPreferenceRejectsUnsafeNamesBeforeAnyDiscovery(string requestedName)
    {
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            discoveries++;
            return [];
        });

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            catalog.ResolveApplicationPreference(requestedName));

        Assert.Equal(InstalledApplicationErrorCodes.TargetNotAllowed, error.Code);
        Assert.Equal(0, discoveries);
    }

    [Theory]
    [InlineData("设置", "ms-settings:")]
    [InlineData("显示设置", "ms-settings:display")]
    [InlineData("声音设置", "ms-settings:sound")]
    [InlineData("蓝牙和设备", "ms-settings:bluetooth")]
    [InlineData("网络状态", "ms-settings:network-status")]
    [InlineData("已安装的应用", "ms-settings:appsfeatures")]
    [InlineData("存储设置", "ms-settings:storagesense")]
    [InlineData("系统信息", "ms-settings:about")]
    public void ResolveByDisplayName_AllowsOnlyFixedSafeSettingsAliases(
        string requestedName,
        string expectedTarget)
    {
        var catalog = new InstalledApplicationCatalog(() => []);

        var application = catalog.ResolveByDisplayName(requestedName);

        Assert.Equal(expectedTarget, application.LaunchTarget);
    }

    [Theory]
    [InlineData("账户设置")]
    [InlineData("登录选项")]
    [InlineData("Windows 安全中心")]
    [InlineData("恢复设置")]
    [InlineData("激活设置")]
    [InlineData("开发者设置")]
    [InlineData("Windows 更新")]
    [InlineData("Windows Security")]
    [InlineData("Sign-in options")]
    public void ResolveByDisplayName_RejectsSensitiveSettingsPages(string requestedName)
    {
        var catalog = new InstalledApplicationCatalog(() => []);

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            catalog.ResolveByDisplayName(requestedName));

        Assert.Equal("application_target_not_allowed", error.Code);
    }

    [Fact]
    public void PossessivePrefixDoesNotOverrideAnExactRegisteredName()
    {
        var root = CreateRoot("possessive");
        try
        {
            var literal = Registration("我的网易云", root, "literal.exe");
            var music = Registration("网易云音乐", root, "cloudmusic.exe");
            var catalog = new InstalledApplicationCatalog(() => [literal, music]);
            Assert.Equal(literal.Target, catalog.ResolveByDisplayName("我的网易云").LaunchTarget);
            Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName("我的不存在的程序"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(@"以管理员身份运行C:\Gate4A\admin.exe")]
    [InlineData(@"以管理员身份运行 C:\Gate4A\admin.exe")]
    [InlineData(@"C:\Gate4A\admin.exe")]
    [InlineData("admin.exe")]
    [InlineData("cmd /c calc")]
    [InlineData("powershell.exe")]
    [InlineData("\"C:\\Gate4A\\admin.exe\" --unsafe")]
    [InlineData("“admin.exe” --unsafe")]
    [InlineData("执行命令时使用运行")]
    [InlineData(@"桌面上的C:\Gate4A\admin.exe")]
    [InlineData("我的桌面上的admin.exe")]
    [InlineData("桌面上的cmd /c calc")]
    [InlineData("桌面上的以管理员身份运行")]
    public void ResolveByDisplayName_RejectsUnsafeRequestSyntaxBeforeFuzzyMatching(
        string requestedName)
    {
        var root = CreateRoot("unsafe-request");
        try
        {
            ApplicationRegistration[] registrations =
            [
                new ApplicationRegistration(
                    "运行",
                    "Microsoft.Windows.Shell.RunDialog",
                    ApplicationRegistrationSource.AppsFolder),
                Registration("admin.exe", root, "admin.exe"),
                Registration("PowerShell", root, "powershell.exe")
            ];
            var catalog = new InstalledApplicationCatalog(() => registrations);

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName(requestedName));

            Assert.Equal(InstalledApplicationErrorCodes.TargetNotAllowed, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_KeepsChromeAliasAmbiguousAcrossDifferentTargets()
    {
        var root = CreateRoot("chrome-ambiguous");
        try
        {
            var catalog = new InstalledApplicationCatalog(() =>
            [
                Registration("Google Chrome", root, "chrome-user.exe"),
                Registration("Google Chrome", root, "chrome-machine.exe")
            ]);

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName("谷歌浏览器"));

            Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveByDisplayName_AllowsExactRunDialogNameWithoutInferringItFromLongText()
    {
        var catalog = new InstalledApplicationCatalog(() =>
        [
            new ApplicationRegistration(
                "运行",
                "Microsoft.Windows.Shell.RunDialog",
                ApplicationRegistrationSource.AppsFolder)
        ]);

        var application = catalog.ResolveByDisplayName("运行");

        Assert.Equal("运行", application.DisplayName);
        Assert.Equal(
            "shell:AppsFolder\\Microsoft.Windows.Shell.RunDialog",
            application.LaunchTarget);
    }

    [Fact]
    public void ResolveByDisplayName_UnknownApplicationStillFailsClosed()
    {
        var catalog = new InstalledApplicationCatalog(() => []);

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            catalog.ResolveByDisplayName("海星工具987654321"));

        Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public void ResolveByDisplayName_DoesNotExtractShortApplicationNameFromLongRequest()
    {
        var root = CreateRoot("long-request");
        try
        {
            var registration = Registration("运行", root, "run-dialog.exe");
            var catalog = new InstalledApplicationCatalog(() => [registration]);

            var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
                catalog.ResolveByDisplayName("把运行作为附加说明的其他目标"));

            Assert.Equal(InstalledApplicationErrorCodes.NotFound, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discovery_ResolvesSafeShortcutAndRejectsUnsafeLaunchTargets()
    {
        var root = CreateRoot("unsafe");
        var safeExe = CreateExecutable(root, "safe.exe");
        var arguedExe = CreateExecutable(root, "argued.exe");
        var elevatedExe = CreateExecutable(root, "elevated.exe");
        var manifestElevatedExe = CreateExecutable(root, "manifest-elevated.exe");
        File.WriteAllText(
            manifestElevatedExe + ".manifest",
            "<requestedExecutionLevel level=\"requireAdministrator\" />");
        var document = CreateFile(root, "document.txt");
        var script = CreateFile(root, "script.ps1");
        var uninstall = CreateExecutable(root, "unins000.exe");
        try
        {
            var shortcuts = new Dictionary<string, ShortcutResolution>(StringComparer.Ordinal)
            {
                [Path.Combine(root, "safe.lnk")] = new(safeExe),
                [Path.Combine(root, "argued.lnk")] = new(arguedExe, "--unsafe"),
                [Path.Combine(root, "elevated.lnk")] = new(elevatedExe, RequiresElevation: true),
                [Path.Combine(root, "document.lnk")] = new(document)
            };
            var catalog = new InstalledApplicationCatalog(
                () =>
                [
                    new("安全快捷方式", Path.Combine(root, "safe.lnk"), ApplicationRegistrationSource.Shortcut),
                    new("带参数快捷方式", Path.Combine(root, "argued.lnk"), ApplicationRegistrationSource.Shortcut),
                    new("提权快捷方式", Path.Combine(root, "elevated.lnk"), ApplicationRegistrationSource.Shortcut),
                    new("提权程序", manifestElevatedExe, ApplicationRegistrationSource.AppPath),
                    new("文档快捷方式", Path.Combine(root, "document.lnk"), ApplicationRegistrationSource.Shortcut),
                    new("脚本", script, ApplicationRegistrationSource.AppPath),
                    new("网络程序", @"\\server\share\app.exe", ApplicationRegistrationSource.AppPath),
                    new("卸载程序", uninstall, ApplicationRegistrationSource.AppPath),
                    new("任意 URI", "ms-settings:windowsupdate", ApplicationRegistrationSource.AppPath),
                    new("任意 Shell", "shell:Downloads", ApplicationRegistrationSource.AppsFolder),
                    new("可信 AppsFolder", "shell:AppsFolder\\Vendor.App_123!App", ApplicationRegistrationSource.AppsFolder),
                    new("原始 AUMID", "Vendor.Raw_456!App", ApplicationRegistrationSource.AppsFolder)
                ],
                path => shortcuts.TryGetValue(path, out var resolved) ? resolved : null);

            var safeShortcut = catalog.ResolveByDisplayName("安全快捷方式");
            var appsFolder = catalog.ResolveByDisplayName("可信 AppsFolder");

            Assert.Equal(safeExe, safeShortcut.LaunchTarget);
            Assert.Equal("shell:AppsFolder\\Vendor.App_123!App", appsFolder.LaunchTarget);
            Assert.Equal(
                "shell:AppsFolder\\Vendor.Raw_456!App",
                catalog.ResolveByDisplayName("原始 AUMID").LaunchTarget);
            Assert.DoesNotContain(catalog.GetApplications(), item => item.DisplayName is
                "带参数快捷方式" or "提权快捷方式" or "提权程序" or "文档快捷方式" or
                "脚本" or "网络程序" or "卸载程序" or "任意 URI" or "任意 Shell");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectUniqueApplication_PrefersSingleRegisteredExecutableOverShortcut()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "sample.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var selected = InstalledApplicationCatalog.SelectUniqueApplication(
            [
                new KnownDesktopApplication("installed-exe", "示例应用", executable),
                new KnownDesktopApplication("installed-link", "示例应用", Path.Combine(root, "示例应用.lnk"))
            ]);

            Assert.NotNull(selected);
            Assert.Equal(executable, selected.LaunchTarget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectUniqueApplication_RejectsTwoDifferentRegisteredExecutables()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.exe");
        var second = Path.Combine(root, "second.exe");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);
        try
        {
            var selected = InstalledApplicationCatalog.SelectUniqueApplication(
            [
                new KnownDesktopApplication("installed-first", "同名应用", first),
                new KnownDesktopApplication("installed-second", "同名应用", second)
            ]);

            Assert.Null(selected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ApplicationRegistration Registration(
        string displayName,
        string root,
        string executableName)
    {
        var path = Path.Combine(root, executableName);
        File.Copy(Environment.ProcessPath!, path);
        return new ApplicationRegistration(
            displayName,
            path,
            ApplicationRegistrationSource.AppPath);
    }

    private static string CreateRoot(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateFile(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static string CreateExecutable(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        File.Copy(Environment.ProcessPath!, path);
        return path;
    }
}
