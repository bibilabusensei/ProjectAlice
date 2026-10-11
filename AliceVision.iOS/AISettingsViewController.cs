using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CoreGraphics;
using UIKit;

namespace AliceVision.iOS;

internal sealed class AISettingsViewController : UIViewController
{
    private readonly Action _saved;
    private readonly UIScrollView _scroll = new();
    private readonly UITextField _endpoint = Field("完整 HTTPS chat/completions URL");
    private readonly UITextField _model = Field("输入视觉模型 ID，或点下方获取模型");
    private readonly UITextField _key = Field("API Key（可留空；由接口决定是否允许）");
    private readonly UITextField _interval = Field("10–120 秒，默认 15 秒");
    private readonly UITextView _persona = new();
    private readonly UIButton _provider = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _models = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _save = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _close = UIButton.FromType(UIButtonType.System);
    private readonly UILabel _status = new() { Lines = 0, Font = UIFont.SystemFontOfSize(13)! };
    private readonly List<(UIView View, double Height)> _rows = new();
    private readonly CancellationTokenSource _lifetime = new();
    private string _providerId = "custom", _loadedEndpoint = "";
    private bool _loadingModels, _closed;

    private static readonly (string Id, string Label, string Endpoint)[] Providers =
    [
        ("custom", "自定义兼容接口", ""),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/chat/completions"),
        ("gemini", "Google Gemini 兼容接口", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")
    ];

    public AISettingsViewController(Action saved) => _saved = saved;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;
        _scroll.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
        View.AddSubview(_scroll);
        var current = CompanionApiSettings.Current;
        _providerId = current.ProviderId;
        _loadedEndpoint = current.Endpoint;
        _endpoint.Text = current.Endpoint;
        _endpoint.KeyboardType = UIKeyboardType.Url;
        _model.Text = current.Model;
        _key.Text = current.ApiKey;
        _key.SecureTextEntry = true;
        _interval.Text = current.IntervalSeconds.ToString();
        _interval.KeyboardType = UIKeyboardType.NumberPad;
        _persona.Text = current.Persona;
        _persona.Font = UIFont.SystemFontOfSize(15)!;
        _persona.BackgroundColor = UIColor.SecondarySystemBackground;
        _persona.Layer.CornerRadius = 8;
        _endpoint.EditingChanged += (_, _) =>
        {
            // Editing the destination never carries a previously stored bearer key to a new server.
            if (!string.Equals(_loadedEndpoint, _endpoint.Text, StringComparison.Ordinal))
            {
                _key.Text = "";
                _loadedEndpoint = "";
            }
        };
        _provider.TouchUpInside += (_, _) => ChooseProvider();
        _models.SetTitle("从此接口获取模型列表", UIControlState.Normal);
        _models.TouchUpInside += async (_, _) => await FetchModelsAsync();
        _save.SetTitle("保存配置（不会自动启动 AI）", UIControlState.Normal);
        _save.TouchUpInside += (_, _) => Save();
        _close.SetTitle("关闭", UIControlState.Normal);
        _close.TouchUpInside += (_, _) => Close();
        UpdateProviderTitle();
        Add(Label("AI · 视觉空间伙伴", 24), 38);
        Add(_close, 38);
        Add(Label("服务商（不是免费额度保证）"), 26); Add(_provider, 44);
        Add(Label("接口：请填完整 chat/completions 地址"), 28); Add(_endpoint, 44);
        Add(Label("模型：必须支持图像输入"), 26); Add(_model, 44);
        Add(Label("API Key：只保存在此手机的钥匙串"), 26); Add(_key, 44);
        Add(_models, 44);
        Add(Label("自主决策间隔（秒）"), 26); Add(_interval, 44);
        Add(Label("角色性格 / 自主活动目标"), 26); Add(_persona, 100);
        Add(_status, 62); Add(_save, 48);
        Add(Label("启动 AI 前会再次确认：将定期上传当前 AR 画面（最长边 768 像素）。相机可能包含人脸、屏幕等私人信息；请只使用可信接口。请求可能产生服务商费用。\n\nAI 只能选择应用提供的动作和经过 LiDAR、活动区与禁区检查的目标，不能执行代码或控制真实设备。未配置 API 仍能导入模型和手动做动作。", 13), 168);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var inset = View!.SafeAreaInsets;
        _scroll.Frame = new CGRect(0, inset.Top, View.Bounds.Width, View.Bounds.Height - inset.Top - inset.Bottom);
        double y = 14, width = Math.Max(120, View.Bounds.Width - 32);
        foreach (var row in _rows)
        {
            row.View.Frame = new CGRect(16, y, width, row.Height);
            y += row.Height + 6;
        }
        _scroll.ContentSize = new CGSize(View.Bounds.Width, y + 24);
    }

    private void Add(UIView view, double height) { _scroll.AddSubview(view); _rows.Add((view, height)); }
    private static UILabel Label(string text, float size = 15) => new()
    { Text = text, Font = UIFont.SystemFontOfSize(size)!, TextColor = UIColor.Label, Lines = 0 };
    private static UITextField Field(string placeholder) => new()
    {
        Placeholder = placeholder, BorderStyle = UITextBorderStyle.RoundedRect,
        AutocorrectionType = UITextAutocorrectionType.No, AutocapitalizationType = UITextAutocapitalizationType.None,
        Font = UIFont.SystemFontOfSize(14)!, ClearButtonMode = UITextFieldViewMode.WhileEditing
    };
    private void UpdateProviderTitle() => _provider.SetTitle(
        Providers.FirstOrDefault(p => p.Id == _providerId).Label ?? "自定义兼容接口", UIControlState.Normal);

    private void ChooseProvider()
    {
        var sheet = UIAlertController.Create("选择 API 服务商", "模型和 Key 由你自己的服务商提供；不内置任何账号。", UIAlertControllerStyle.ActionSheet);
        foreach (var provider in Providers)
            sheet.AddAction(UIAlertAction.Create(provider.Label, UIAlertActionStyle.Default, _ =>
            {
                _providerId = provider.Id;
                _endpoint.Text = provider.Endpoint;
                _loadedEndpoint = "";
                _key.Text = ""; _model.Text = "";
                _status.Text = "已切换服务商，请重新填写 Key 和视觉模型。";
                UpdateProviderTitle();
            }));
        sheet.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        Anchor(sheet, _provider);
        PresentViewController(sheet, true, null);
    }

    private async Task FetchModelsAsync()
    {
        if (_loadingModels) return;
        string endpoint = _endpoint.Text?.Trim() ?? "", key = _key.Text?.Trim() ?? "";
        if (!CompanionApiSettings.ValidEndpoint(endpoint, out var uri) ||
            !uri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            _status.Text = "此地址无法推导 /models，请手动填模型 ID。完整接口需以 /chat/completions 结尾。";
            return;
        }
        if (key.Length > 4096 || key.Any(char.IsControl)) { _status.Text = "Key 格式无效。"; return; }
        _loadingModels = true; _models.Enabled = false;
        _provider.Enabled = false; _endpoint.Enabled = false; _key.Enabled = false;
        _status.Text = $"正在向 {uri.Host} 请求模型列表（只发送此处填写的 Key，不发送相机画面）…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var modelUri = new Uri(uri.AbsoluteUri[..^"chat/completions".Length] + "models");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, modelUri);
            if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (_closed) return;
            if (!response.IsSuccessStatusCode) { _status.Text = $"列表请求未成功（HTTP {(int)response.StatusCode}），可手动输入模型 ID。"; return; }
            if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024) throw new InvalidDataException();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + read > 2 * 1024 * 1024) throw new InvalidDataException();
                memory.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var ids = new List<string>();
            if (document.RootElement.TryGetProperty("data", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var model in models.EnumerateArray().Take(2048))
                {
                    if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (_providerId == "openrouter" && !HasImageModality(model)) continue;
                    string value = id.GetString() ?? "";
                    if (value.Length is > 0 and <= 160 && !value.Any(char.IsControl)) ids.Add(value);
                }
            if (_closed) return;
            var ordered = ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0) { _status.Text = "没有取得可选模型，请手动填写视觉模型 ID。"; return; }
            _status.Text = _providerId == "openrouter" ? $"找到 {ordered.Length} 个支持图像输入的模型。" : $"找到 {ordered.Length} 个模型，请自行确认所选模型支持图像。";
            ChooseModelFilter(ordered);
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "请求取消或超时，可手动填写模型。"; }
        catch (Exception) { if (!_closed) _status.Text = "无法读取模型列表。请检查接口 / 网络，或手动填写模型 ID。"; }
        finally
        {
            _loadingModels = false;
            if (!_closed) { _models.Enabled = true; _provider.Enabled = true; _endpoint.Enabled = true; _key.Enabled = true; }
        }
    }

    private static bool HasImageModality(JsonElement model) =>
        model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
        architecture.TryGetProperty("input_modalities", out var modes) && modes.ValueKind == JsonValueKind.Array &&
        modes.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "image");

    private void ChooseModelFilter(string[] ids)
    {
        var prompt = UIAlertController.Create("选择视觉模型", "输入关键词筛选（例如 gemma）；留空显示前 25 个。也可关闭后手动填写 ID。", UIAlertControllerStyle.Alert);
        prompt.AddTextField(field => field.Placeholder = "模型关键词");
        prompt.AddAction(UIAlertAction.Create("查看模型", UIAlertActionStyle.Default, _ =>
        {
            string query = prompt.TextFields?[0].Text?.Trim() ?? "";
            var matches = ids.Where(id => id.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(25).ToArray();
            if (matches.Length == 0) { _status.Text = "没有匹配模型，请换个关键词或手动输入。"; return; }
            var sheet = UIAlertController.Create("选择模型 ID", "仅列出前 25 个匹配项。必须支持视觉；用量以服务商为准。", UIAlertControllerStyle.ActionSheet);
            foreach (string id in matches)
                sheet.AddAction(UIAlertAction.Create(id, UIAlertActionStyle.Default, _ => _model.Text = id));
            sheet.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
            Anchor(sheet, _models);
            PresentViewController(sheet, true, null);
        }));
        prompt.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        PresentViewController(prompt, true, null);
    }

    private static void Anchor(UIAlertController sheet, UIView source)
    {
        if (sheet.PopoverPresentationController is { } popover)
        { popover.SourceView = source; popover.SourceRect = source.Bounds; }
    }

    private void Save()
    {
        try
        {
            View!.EndEditing(true);
            if (!int.TryParse(_interval.Text, out int interval) || interval is < 10 or > 120)
            { _status.Text = "间隔请输入 10–120 秒。"; return; }
            CompanionApiSettings.Save(new(_providerId, _endpoint.Text?.Trim() ?? "", _model.Text?.Trim() ?? "",
                _key.Text?.Trim() ?? "", interval, _persona.Text ?? ""));
            _saved(); Close();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _status.Text = ex.Message; }
    }

    private void Close()
    {
        _closed = true; _lifetime.Cancel(); _key.Text = "";
        DismissViewController(true, null);
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        // Swiping down the sheet must cancel its credential-bearing request too.
        if (IsBeingDismissed || PresentingViewController is null) { _closed = true; _lifetime.Cancel(); _key.Text = ""; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
