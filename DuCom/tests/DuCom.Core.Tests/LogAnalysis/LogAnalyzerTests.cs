using System.Text;
using DuCom.Core.LogAnalysis;

namespace DuCom.Core.Tests.LogAnalysis;

public sealed class LogAnalyzerTests
{
    [Fact]
    public void BuiltInRulePackContainsAllSearchTextEntries()
    {
        IReadOnlyList<LogAnalyzerRule> rules = LogAnalyzerRuleService.LoadDefaults();

        Assert.Equal(95, rules.Count);
        Assert.Contains(rules, rule => rule.Pattern == "ASSERT" && rule.Category == "异常");
        Assert.Contains(rules, rule => rule.Pattern == "04 03 0b" && rule.Name == "Connection Complete event");
        Assert.All(rules, rule => Assert.True(rule.IsEnabled));
    }

    [Fact]
    public void AnalyseDocImportPreservesLiteralTextAndColors()
    {
        const string xml = "<AnalyseDoc><SearchText doSearch=\"false\" color=\"blue\" bgColor=\"yellow\" comment=\"Notify\">[Notify]</SearchText></AnalyseDoc>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));

        LogAnalyzerRule rule = Assert.Single(AnalyseDocRuleImporter.Import(stream));

        Assert.Equal("[Notify]", rule.Pattern);
        Assert.Equal("Notify", rule.Name);
        Assert.Equal((byte)37, rule.ForegroundR);
        Assert.Equal((byte)250, rule.BackgroundR);
        Assert.True(rule.IsEnabled);
        Assert.False(rule.IncludeInAnalysis);
    }

    [Fact]
    public void HighlightOnlyRulesDoNotGenerateKeywordsOrComments()
    {
        const string xml = "<AnalyseDoc><SearchText doSearch=\"false\" color=\"blue\">communication</SearchText></AnalyseDoc>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
        LogAnalyzerRecord record = new LogAnalyzerParser(AnalyseDocRuleImporter.Import(stream)).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "communication_receive_register_callback register receive callback success");

        Assert.Single(record.MatchedRules);
        Assert.Empty(record.Keywords);
        Assert.Empty(record.ChineseComment);
    }

    [Theory]
    [InlineData("CHIP=best1307p", "BES Chip", "BES 芯片/固件目标：best1307p")]
    [InlineData("FLASH_SIZE=0x200000", "Flash Size", "Flash 容量：2 MiB（0x200000）")]
    [InlineData("METAL_ID: 0", "BES Metal ID", "芯片硅版本编号：0")]
    [InlineData("localname=BESIX Pods, namelen=10", "Bluetooth Local Name", "蓝牙本地名称：BESIX Pods")]
    [InlineData("local_addr: 37:34:*:*:*:12", "Bluetooth Local Address", "本机蓝牙地址：37:34:*:*:*:12（按日志顺序）")]
    [InlineData("codec_dac_dc_auto_load: start: open_af=1, reboot=0", "DAC Calibration Load", "加载 DAC 直流校准：音频框架 已打开，重启标志 0")]
    [InlineData("app_key_init", "Application Key Init", "应用按键模块初始化")]
    public void ParserAnnotatesBesStartupInformation(string text, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(keyword, record.Keywords);
        Assert.Contains(comment, record.ChineseComment);
    }

    [Fact]
    public void ParserDecodesA2dpCurrentStreamStateWithoutGuessingUnknownFields()
    {
        const string text = "a2dp_state: curr_playing_a2dp ff curr_a2dp_stream_id 0 int_a2dp ff";
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains("A2DP Current Stream", record.Keywords);
        Assert.Contains("0xFF（未选择/无效哨兵，枚举未确认）", record.ChineseComment);
        Assert.Contains("流 ID 0", record.ChineseComment);
        Assert.Contains("内部字段 0xFF", record.ChineseComment);
    }

    [Theory]
    [InlineData("04 0e 04 05 03 0c 00", "HCI Reset：执行成功")]
    [InlineData("04 0e 04 05 ff fc 01", "BES Vendor Specific 0xFCFF：执行失败，状态 0x01 未知 HCI 命令")]
    public void CommandCompleteExplainsCompletedCommandAndStatus(string text, string expectedComment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM43", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(expectedComment, record.ChineseComment);
        Assert.DoesNotContain("蓝牙命令执行完成", record.ChineseComment);
    }

    [Theory]
    [InlineData("KERNEL=FREERTOS", "RTOS Kernel", "实时操作系统内核：FREERTOS")]
    [InlineData("[UIVER]Cpu Freq     = 208M", "CPU Frequency", "当前 CPU 主频：208 MHz")]
    [InlineData("[A2DP_PLAYER] sysfreq calc : 96000000", "Calculated System Frequency", "系统频率计算结果：96 MHz（96000000 Hz）")]
    [InlineData("[24] f=14", "Sysfreq User Request", "系统频率请求者 24：频率枚举 14（芯片相关原始值）")]
    [InlineData("top_user=20", "Sysfreq Top User", "当前最高系统频率请求者：20（原始请求者 ID）")]
    [InlineData("BESBT_STACK_SIZE 1c00 7168", "Bluetooth Stack Size", "蓝牙协议栈线程栈大小：7168 字节（0x1C00）")]
    [InlineData("key_handler_thread initialized successfully", "Key Handler Thread Initialized", "按键处理线程初始化成功")]
    [InlineData("cp_accel_open, task id = 1, cp_state = 0 init 0", "CP Task Open", "协处理器加速任务开启：任务 ID 1，CP 状态 0，初始化标志 0")]
    public void ParserAnnotatesCpuAndThreadManagement(string text, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(keyword, record.Keywords);
        Assert.Contains(comment, record.ChineseComment);
    }

    [Theory]
    [InlineData("CPU USAGE: busy=73 light=27 sys_deep=0 chip_deep=0", "CPU Usage", "CPU利用率：忙碌 73%，轻度休眠 27%，系统深度休眠 0%，芯片深度休眠 0%")]
    [InlineData("CP CPU USAGE: busy=14 sleep=86", "CP CPU Usage", "协处理器利用率：忙碌 14%，休眠 86%")]
    public void ParserAnnotatesCpuUsageFields(string text, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(keyword, record.Keywords);
        Assert.Contains(comment, record.ChineseComment);
    }

    [Fact]
    public void RuleServiceSilentlyAddsNewDefaultsAndPreservesCustomRules()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "log-analyzer-rules.json");
        Directory.CreateDirectory(directory);
        try
        {
            LogAnalyzerRuleService service = new(path);
            LogAnalyzerRule custom = new(Guid.NewGuid(), "custom", "用户规则", "自定义", "CUSTOM_TRACE", false, false,
                1, 2, 3, 4, 5, 6);
            service.Save([custom]);

            IReadOnlyList<LogAnalyzerRule> rules = service.Load();

            Assert.Contains(rules, rule => rule.Id == custom.Id && rule.Pattern == custom.Pattern && !rule.IsEnabled);
            Assert.Contains(rules, rule => rule.Pattern == "TWS-STATE:" && rule.Comment == "TWS 状态机");
            Assert.Equal(LogAnalyzerRuleService.LoadDefaults().Count + 1, rules.Count);
            Assert.Contains("TWS-STATE:", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RuleServiceRefreshesBuiltInMetadataButPreservesUserSwitches()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "log-analyzer-rules.json");
        Directory.CreateDirectory(directory);
        try
        {
            Guid id = Guid.NewGuid();
            LogAnalyzerRuleService service = new(path);
            service.Save([new LogAnalyzerRule(id, "old", "旧注释", "旧分类", "ASSERT", true, false,
                1, 2, 3, 4, 5, 6)]);

            LogAnalyzerRule rule = Assert.Single(service.Load(), rule => rule.Pattern == "ASSERT");

            Assert.Equal(id, rule.Id);
            Assert.Equal("断言异常", rule.Comment);
            Assert.Equal("异常", rule.Category);
            Assert.True(rule.IsCaseSensitive);
            Assert.False(rule.IsEnabled);
            Assert.Equal((byte)250, rule.ForegroundR);
            Assert.Equal((byte)220, rule.BackgroundR);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RuleServiceRepairsInvalidJsonWithLatestDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "log-analyzer-rules.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "not-json");

            IReadOnlyList<LogAnalyzerRule> rules = new LogAnalyzerRuleService(path).Load();

            Assert.Equal(95, rules.Count);
            Assert.Contains(rules, rule => rule.Pattern == "TWS-STATE:");
            Assert.StartsWith("[", File.ReadAllText(path).TrimStart(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParserExtractsBesFieldsAndKeywordMatches()
    {
        LogAnalyzerRule rule = new(Guid.NewGuid(), "assert", "断言异常", "异常", "ASSERT", false, true, null, null, null, null, null, null);
        LogAnalyzerParser parser = new([rule]);
        DateTimeOffset received = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM7", "L", received, TimeSpan.FromMilliseconds(10),
            "[12:34:56.789]       741/E/NONE  /  1 | ASSERT failed");

        Assert.Equal("ERROR", record.Level);
        Assert.Equal("NONE", record.Module);
        Assert.Equal("ASSERT failed", record.Message);
        Assert.Equal("断言异常", record.ChineseComment);
        Assert.Equal(new TimeSpan(12, 34, 56) + TimeSpan.FromMilliseconds(789), record.BaseDisplayTime.TimeOfDay);
        Assert.Equal(new TimeSpan(12, 34, 56) + TimeSpan.FromMilliseconds(799), record.DisplayTime.TimeOfDay);
        Assert.Single(record.MatchedRules);
    }

    [Fact]
    public void UnmatchedStructuredLineHasNoComment()
    {
        LogAnalyzerParser parser = new([]);

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM7", "L", DateTimeOffset.Now, TimeSpan.Zero,
            "741/I/BT_APP  /  1 | normal trace");

        Assert.Empty(record.ChineseComment);
    }

    [Theory]
    [InlineData("00", "关闭蓝牙可连接、关闭蓝牙可发现")]
    [InlineData("01", "关闭蓝牙可连接、打开蓝牙可发现")]
    [InlineData("02", "打开蓝牙可连接、关闭蓝牙可发现")]
    [InlineData("03", "打开蓝牙可连接、打开蓝牙可发现")]
    public void ParserDecodesWriteScanEnable(string value, string expected)
    {
        LogAnalyzerParser parser = new([]);

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM42", string.Empty, DateTimeOffset.Now, TimeSpan.Zero,
            $"[18:28:07.226] 01 1A  0c 01 {value}");

        Assert.Equal("蓝牙连接", record.Module);
        Assert.Equal("HCI Write Scan Enable", record.Keywords);
        Assert.Equal(expected, record.ChineseComment);
    }

    [Fact]
    public void ParameterizedHciCommandDoesNotAlsoEmitGenericXmlComment()
    {
        LogAnalyzerParser parser = new(LogAnalyzerRuleService.LoadDefaults());

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM42", string.Empty, DateTimeOffset.Now, TimeSpan.Zero,
            "01 1a 0c 01 00");

        Assert.Equal("关闭蓝牙可连接、关闭蓝牙可发现", record.ChineseComment);
        Assert.DoesNotContain("设置蓝牙可连接/可发现状态", record.ChineseComment);
    }

    [Theory]
    [InlineData("TWS-STATE:DISCONNECTED EVENT=ENTRY", "TWS 状态：未连接")]
    [InlineData("TWS-STATE:CONNECTING EVENT=START", "TWS 状态：连接中")]
    [InlineData("TWS-STATE:CONNECTED EVENT=ENTRY", "TWS 状态：ACL 已连接")]
    [InlineData("TWS-STATE:W4-SHAREINFO EVENT=START", "TWS 状态：共享信息同步中")]
    [InlineData("ACCESS-CTL:Judgement:tws mode and tws not connect, enable pscan", "TWS 未连接，已开启 Page Scan 等待对耳连接")]
    public void ParserUsesConciseTwsStateComments(string text, string expected)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(expected, record.ChineseComment);
    }

    [Theory]
    [InlineData("avrcp_key play", "蓝牙媒体控制", "AVRCP Play", "AVRCP 开始播放")]
    [InlineData("HFP AT+CHUP", "蓝牙通话", "HFP Hang Up", "HFP 挂断电话")]
    [InlineData("RFCOMM DLCI open", "蓝牙串口", "SPP Channel Open", "SPP RFCOMM 串口通道已打开")]
    [InlineData("A2DP stream start", "蓝牙音乐", "A2DP Stream Started", "A2DP 蓝牙音乐流开始")]
    [InlineData("BLE GATT notification", "低功耗蓝牙", "GATT Notification", "GATT 特征通知")]
    public void ParserAnnotatesStandardBluetoothProfiles(string text, string module, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal(module, record.Module);
        Assert.Equal(keyword, record.Keywords);
        Assert.Equal(comment, record.ChineseComment);
    }

    [Fact]
    public void ParserExpandsBesProfileStateConnections()
    {
        const string text = "ONE / 12 | MASTER MOBILE profile_state: [d0] a2rvp con: 1, 1, a2dp con: 1, 1, hfp conn: 1, 0";

        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal("蓝牙配置状态", record.Module);
        Assert.Equal("Bluetooth Profile State", record.Keywords);
        Assert.Equal("设备槽 d0，当前主机；AVRCP：连接标志 1，状态码 1；A2DP：连接标志 1，状态码 1；HFP：连接标志 1，状态码 0", record.ChineseComment);
    }

    [Fact]
    public void ParserMergesMultipleProtocolsAndRuleComments()
    {
        LogAnalyzerRule mediaRule = new(Guid.NewGuid(), "media_active", "媒体活动状态", "音频", "media_active", false, true,
            null, null, null, null, null, null);
        const string text = "AVRCP pause notify; A2DP stream suspend codec SBC; HFP +BCS SCO connected; media_active";

        LogAnalyzerRecord record = new LogAnalyzerParser([mediaRule]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains("AVRCP Pause", record.Keywords);
        Assert.Contains("AVRCP Notification", record.Keywords);
        Assert.Contains("A2DP Stream Suspended", record.Keywords);
        Assert.Contains("A2DP Codec", record.Keywords);
        Assert.Contains("HFP Codec Negotiation", record.Keywords);
        Assert.Contains("HFP Audio Connected", record.Keywords);
        Assert.Contains("AVRCP 暂停播放", record.ChineseComment);
        Assert.Contains("AVRCP 状态通知", record.ChineseComment);
        Assert.Contains("A2DP 蓝牙音乐流暂停", record.ChineseComment);
        Assert.Contains("A2DP 音乐编解码配置发生变化", record.ChineseComment);
        Assert.Contains("HFP 通话音频编解码协商", record.ChineseComment);
        Assert.Contains("HFP 通话音频链路已连接", record.ChineseComment);
        Assert.Contains("媒体活动状态", record.ChineseComment);
    }

    [Fact]
    public void ParserMergesHciPacketAndProfileText()
    {
        const string text = "HCI packet: 04 03 0b 00 01 00 00 00 00 00 00 00 00 00";

        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains("HCI Connection Complete", record.Keywords);
        Assert.Contains("蓝牙连接完成", record.ChineseComment);
    }

    [Fact]
    public void HciDisconnectionTextIsNotMisreadAsConnectionComplete()
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "HCI Disconnection Complete");

        Assert.Contains("HCI Disconnection Complete", record.Keywords);
        Assert.DoesNotContain("HCI Connection Complete", record.Keywords);
    }

    [Fact]
    public void ParserDecodesLeMetaSubevent()
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "04 3e 02 0c 00");

        Assert.Equal("低功耗蓝牙", record.Module);
        Assert.Equal("HCI LE PHY Update Complete", record.Keywords);
        Assert.Equal("BLE PHY 更新完成", record.ChineseComment);
    }

    [Fact]
    public void ParserDecodesPrefixedHciBytesUsedByBesLogs()
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "[18:28:07.226] 0x01 0x1a 0x0c 0x01 0x02");

        Assert.Equal("HCI Write Scan Enable", record.Keywords);
        Assert.Equal("打开蓝牙可连接、关闭蓝牙可发现", record.ChineseComment);
    }

    [Fact]
    public void HciDecoderRejectsHexValuesEmbeddedInOrdinaryTrace()
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "ibrt_conn_global_handler link =0x2,evt=0x6 error code 0x16 04 03 0b");

        Assert.DoesNotContain("HCI Connection Complete", record.Keywords);
    }

    [Theory]
    [InlineData("TWS-STATE:CONNECTING EVENT=EVT_TWS_CONNECTED", "TWS State", "TWS 状态：连接中")]
    [InlineData("(d1) ::A2DP_EVENT_STREAM_STARTED codec 0 streaming 0 1", "A2DP Stream Started", "设备槽 d1：A2DP 蓝牙音乐流开始")]
    [InlineData("(d0) ::HF_EVENT_AUDIO_CONNECTED codec_id:2", "HFP Audio Connected", "设备槽 d0：HFP 通话音频链路已连接")]
    [InlineData("rfcomm_report_opened: port 3 remote_server_channel 0", "RFCOMM Channel Opened", "RFCOMM 串口通道已打开")]
    [InlineData("BudEvt:input=IN_BOX_OPEN, ori_state=IN_BOX_CLOSED", "Earbud Box State", "盒仓状态由 IN_BOX_CLOSED 变为 IN_BOX_OPEN")]
    [InlineData("[UICHG]ear_putinout = CHARGE_PLUGOUT", "Earbud Charge State", "耳机退出充电")]
    public void ParserDecodesObservedBesFormats(string text, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Contains(keyword, record.Keywords);
        Assert.Contains(comment, record.ChineseComment);
    }

    [Fact]
    public void ProfileDecoderDoesNotTreatSnapshotLabelsAsAvrcpPlay()
    {
        const string text = "a2dp_state: (conn state strming playstat avrcp)=(1 3 1 1 ar 1)";
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.DoesNotContain("AVRCP Play", record.Keywords);
    }

    [Fact]
    public void BleDecoderDoesNotMisreadClassicProfileConnection()
    {
        const string text = "[USER_DRIVE][BLE] enable basic adv reason=first Classic profile connected";
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.DoesNotContain("BLE Connected", record.Keywords);
    }

    [Theory]
    [InlineData("ear_side=left", "左")]
    [InlineData("right_earbud ready", "右")]
    [InlineData("IBRT role changed master to slave", null)]
    public void DeviceSideDetectorUsesOnlyPhysicalSideEvidence(string text, string? expected)
    {
        Assert.Equal(expected, BesDeviceSideDetector.Detect(text));
    }

    [Fact]
    public void SourceAnnotationKeepsPhysicalSideAndTracksExplicitCurrentRole()
    {
        BesSourceAnnotation side = BesSourceAnnotationDetector.Detect("ear_side=left");
        BesSourceAnnotation master = BesSourceAnnotationDetector.Detect("ibrt_role=master");
        BesSourceAnnotation slave = BesSourceAnnotationDetector.Detect("ibrt_role=slave");

        Assert.Equal("左", side.Display);
        Assert.Equal("当前主机", master.Display);
        Assert.Equal("当前从机", slave.Display);
        Assert.Empty(BesSourceAnnotationDetector.Detect("role changed master to slave").Display);
    }

    [Fact]
    public async Task ExportCreatesSuffixedCopyAndOnlyAnnotatesMatchedLines()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "COM42-2026-09-17 18-27-27.146.txt");
        try
        {
            await File.WriteAllLinesAsync(source,
            [
                "[18:28:07.226] 01 1a 0c 01 02",
                "[18:28:08.000] ordinary line",
            ]);

            LogAnalyzerExportResult result = await LogAnalyzerExport.ExportAnnotatedCopyAsync(source, new LogAnalyzerParser([]));
            string[] output = await File.ReadAllLinesAsync(result.OutputPath);

            Assert.Equal(Path.Combine(directory, "COM42-2026-09-17 18-27-27.146-analyse.txt"), result.OutputPath);
            Assert.Contains("//打开蓝牙可连接、关闭蓝牙可发现", output[0]);
            Assert.Equal("[18:28:08.000] ordinary line", output[1]);
            Assert.Equal(1, result.AnnotatedLines);
            Assert.True(File.Exists(source));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportMultipleFilesCreatesOneAnnotatedCopyPerSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "left.txt");
        string second = Path.Combine(directory, "right.log");
        try
        {
            await File.WriteAllTextAsync(first, "[18:28:07.226] 01 1a 0c 01 02\n");
            await File.WriteAllTextAsync(second, "TWS-STATE:CONNECTED EVENT=ENTRY\n");

            IReadOnlyList<LogAnalyzerExportResult> results = await LogAnalyzerExport.ExportAnnotatedCopiesAsync(
                [first, second], new LogAnalyzerParser([]));

            Assert.Equal(2, results.Count);
            Assert.Equal(Path.Combine(directory, "left-analyse.txt"), results[0].OutputPath);
            Assert.Equal(Path.Combine(directory, "right-analyse.log"), results[1].OutputPath);
            Assert.True(File.Exists(results[0].OutputPath));
            Assert.True(File.Exists(results[1].OutputPath));
            Assert.DoesNotContain("TWS-STATE", await File.ReadAllTextAsync(results[0].OutputPath));
            Assert.DoesNotContain("01 1a 0c", await File.ReadAllTextAsync(results[1].OutputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileLoaderOpensObservedBesLogsWithPrefixedHexLines()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                "[14:47:30.882] 0x04 0xa1 0xa7 0xa4 0xa0 0xa0 0xa1 0xa1 0x89",
                "[14:47:34.260] 0x04 0x03 0x00 0x00 0x00 0x00",
                "[14:47:35.000] normal trace",
            ]);

            LogAnalyzerFileLoadResult result = LogAnalyzerFileLoader.Load(
                [new LogAnalyzerFileSource(path, string.Empty)], new LogAnalyzerParser([]), 100, Sequence);

            Assert.Equal(3, result.TotalLineCount);
            Assert.Equal(3, result.Records.Count);
        }
        finally
        {
            File.Delete(path);
        }

        long Sequence() => 1;
    }

    [Theory]
    [InlineData("### ASSERT @ 0x2C012345 ###", "Firmware Assert", "固件主动断言失败，调用地址 0x2C012345")]
    [InlineData("### EXCEPTION ###", "Processor Exception", "检测到处理器异常")]
    [InlineData("FaultInfo    : (BusFault) (HardFault)", "Processor Fault Type", "总线访问异常、硬异常")]
    [InlineData("FaultCause   : (Precise data bus error) (BFAR valid)", "Processor Fault Cause", "精确数据总线错误")]
    [InlineData("FaultCause   : (Stack overflow UsageFault)", "Processor Fault Cause", "硬件栈限制检测到栈溢出")]
    [InlineData("System soft lockup", "System Soft Lockup", "系统软锁死")]
    [InlineData("task audio stack overflow", "FreeRTOS Stack Overflow", "FreeRTOS 检测到任务栈溢出：audio")]
    [InlineData("Find thread hung ", "Thread Hung", "RTX 检测到线程长时间未获得运行机会")]
    [InlineData("cannot malloc memory", "FreeRTOS Malloc Failed", "FreeRTOS 内存分配失败钩子被调用")]
    [InlineData("CRASH ENCOUNTERED", "CrashCatcher Started", "CrashCatcher 已开始输出崩溃核心转储")]
    public void ParserAnnotatesCommonBesFatalFaults(string text, string keyword, string comment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM42", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal("ERROR", record.Level);
        Assert.Contains(keyword, record.Keywords);
        Assert.Contains(comment, record.ChineseComment);
    }

    [Theory]
    [InlineData("osRtxErrorNotify, code: 00000001 object is 0x20001234", "RTX 检测到线程栈越界")]
    [InlineData("osRtxErrorNotify, code: 00000002 object is 0x20001234", "RTX ISR 后处理队列溢出")]
    [InlineData("osRtxErrorNotify, code: 00000003 object is 0x20001234", "RTX 用户定时器回调队列溢出")]
    public void ParserDecodesRtxKernelErrors(string text, string expected)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal("ERROR", record.Level);
        Assert.Contains(expected, record.ChineseComment);
    }

    [Theory]
    [InlineData("[med_malloc] no memory: size=4096", "内存分配失败：请求 4096 字节")]
    [InlineData("System pool in shortage! To allocate size 4096 but free size 1024.", "系统内存池不足：请求 4096 字节，剩余 1024 字节")]
    [InlineData("CORRUPT HEAP: Bad tail at 0x20001000. Expected 0xBAAD5678 got 0x00000000", "堆块尾部保护值损坏")]
    [InlineData("Heap corrupt: 0x20002000", "堆链表或块元数据一致性检查失败")]
    public void ParserDistinguishesMemoryFailures(string text, string expected)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal("ERROR", record.Level);
        Assert.Contains(expected, record.ChineseComment);
    }

    [Fact]
    public void CrashDumpSectionAddressIsNotMisclassifiedAsCrash()
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, "__crash_dump_start: 0x281e9000 length: 0x0");

        Assert.Empty(record.ChineseComment);
        Assert.Equal("OTHER", record.Level);
    }

    [Theory]
    [InlineData("stack_mem=0x20000000 stack_size=1024 sp:0x0100 min_stack_free=100", "WARN", "存在栈溢出风险")]
    [InlineData("task audio stack overflow", "ERROR", "任务栈溢出")]
    public void ParserDistinguishesLowStackRiskFromOverflow(string text, string expectedLevel, string expectedComment)
    {
        LogAnalyzerRecord record = new LogAnalyzerParser([]).Parse(1, "s1", "COM31", string.Empty,
            DateTimeOffset.Now, TimeSpan.Zero, text);

        Assert.Equal(expectedLevel, record.Level);
        Assert.Contains(expectedComment, record.ChineseComment);
    }

    [Fact]
    public void SearchIncludesParsedAndChineseFields()
    {
        LogAnalyzerRule rule = new(Guid.NewGuid(), "connect", "连接完成事件", "连接", "CONNECTED", false, true,
            null, null, null, null, null, null);
        LogAnalyzerRecord record = new(1, "s1", "COM7", "L", DateTimeOffset.Now, DateTimeOffset.Now,
            DateTimeOffset.Now, "INFO", "BT_APP", "CONNECTED", "CONNECTED", [rule]);

        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "连接完成"));
        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "BT_APP"));
        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "COM7"));
        Assert.False(LogAnalyzerOperations.MatchesSearch(record, "not-present"));
    }

    [Fact]
    public void FileLoaderCarriesDateAcrossMidnightAndReportsTruncation()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                "[23:59:59.900] 1/I/MOD / 1 | before",
                "[00:00:00.100] 2/I/MOD / 1 | after",
                "[00:00:00.200] 3/I/MOD / 1 | last",
            ]);
            LogAnalyzerFileLoadResult result = LogAnalyzerFileLoader.Load(
                [new LogAnalyzerFileSource(path, "L")], new LogAnalyzerParser([]), 2, Sequence);

            Assert.Equal(3, result.TotalLineCount);
            Assert.Equal(1, result.TruncatedLineCount);
            Assert.Equal(2, result.Records.Count);
            Assert.True(result.Records[1].DisplayTime > result.Records[0].DisplayTime);
        }
        finally
        {
            File.Delete(path);
        }

        long Sequence() => 1;
    }
}
