using System;
using System.Collections;
using System.Collections.Generic;
using Gua.Core;
using Gua.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using GuaUnityWorldObject = Gua.Unity.GuaWorldObject;
using Object = UnityEngine.Object;

public static class GuaUnityFixture
{
    private static readonly Color Background = new(0.30f, 0.30f, 0.30f, 1f);
    private static readonly Color ButtonNormal = new(0.16f, 0.16f, 0.16f, 1f);
    private static readonly Color ButtonHighlighted = new(0.22f, 0.22f, 0.22f, 1f);
    private static Font legacyFont;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Build()
    {
        GuaUnityAdapterRegistry.Register(new GuaUnityThrowingAdapter());
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        var fixtureWidth = 1280;
        var fixtureHeight = 720;
        var fixtureResolution = Environment.GetEnvironmentVariable("GUA_UNITY_FIXTURE_RESOLUTION");
        if (!string.IsNullOrWhiteSpace(fixtureResolution))
        {
            var parts = fixtureResolution.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out fixtureWidth) || !int.TryParse(parts[1], out fixtureHeight) ||
                fixtureWidth <= 0 || fixtureHeight <= 0)
                throw new ArgumentException("GUA_UNITY_FIXTURE_RESOLUTION must use a positive WIDTHxHEIGHT value.");
        }
        Screen.SetResolution(fixtureWidth, fixtureHeight, FullScreenMode.Windowed);
        legacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var inputMapObject = new GameObject("GuaGameInputFixture");
        var inputMap = inputMapObject.AddComponent<GuaGameInputMap>();
        inputMap.Context = "gameplay";
        inputMap.EnableRawInput = true;
        inputMap.AllowPlayerAgentSemanticInput = true;
        inputMap.AllowPlayerAgentRawInput = true;
        inputMap.Actions.Add(new GuaGameInputAction { Id = "jump", Description = "Jump", ValueType = Gua.Runtime.GuaGameInputValueType.Button, Holdable = true, Bindings = new[] { "Space" }, Category = "movement", Aliases = new[] { "hop" }, Tags = new[] { "gameplay", "air" }, AgentExposure = Gua.Core.GuaAgentExposure.Auto });
        inputMap.Actions.Add(new GuaGameInputAction { Id = "throttle", Description = "Throttle", ValueType = Gua.Runtime.GuaGameInputValueType.Axis1D, HasRange = true, Minimum = -1, Maximum = 1, Holdable = true });
        inputMap.Actions.Add(new GuaGameInputAction { Id = "move", Description = "Move", ValueType = Gua.Runtime.GuaGameInputValueType.Vector2, HasRange = true, Minimum = -1, Maximum = 1, Holdable = true });
        inputMap.Actions.Add(new GuaGameInputAction { Id = "chat", Description = "Chat", ValueType = Gua.Runtime.GuaGameInputValueType.Text, AgentExposure = Gua.Core.GuaAgentExposure.Private });

        if (Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var events = new GameObject("EventSystem");
            events.AddComponent<EventSystem>();
            events.AddComponent<StandaloneInputModule>();
        }

        var cameraObject = new GameObject("BackgroundCamera", typeof(Camera));
        var camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Background;
        camera.cullingMask = 0;
        camera.depth = -100;

        var inactiveScreen = new GameObject("InactiveScreenMarker", typeof(GuaScreen));
        inactiveScreen.GetComponent<GuaScreen>().Value = "inactive-screen";
        inactiveScreen.SetActive(false);

        var canvasObject = new GameObject("GuaUnitySample", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(GuaScreen));
        canvasObject.GetComponent<GuaScreen>().Value = "title";
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 0.5f;

        var background = Rect("Background", canvasObject.transform, Vector2.zero, new Vector2(1280, 720));
        Stretch(background);
        background.gameObject.AddComponent<UnityEngine.UI.Image>().color = Background;

        var titleScreen = Rect("TitleScreen", background, Vector2.zero, new Vector2(360, 300));
        Title("Title", titleScreen, "Gua Unity Sample", new Vector2(0, 104), new Vector2(360, 56), 26);
        var start = Button("StartButton", "start", titleScreen, "Start Game", new Vector2(0, 20));
        var settings = Button("SettingsButton", "settings", titleScreen, "Settings", new Vector2(0, -52));

        var hint = Text("Hint", titleScreen, "Connected through Gua", new Vector2(0, -118), new Vector2(360, 28), 14);
        hint.color = new Color(0.78f, 0.78f, 0.78f, 1f);

        var loadingScreen = Rect("LoadingScreen", background, Vector2.zero, new Vector2(360, 120));
        loadingScreen.gameObject.AddComponent<GuaId>().Value = "loading-screen";
        var loading = Title("Loading", loadingScreen, "Loading...", Vector2.zero, new Vector2(360, 56), 28);
        loading.gameObject.AddComponent<GuaId>().Value = "loading";
        loadingScreen.gameObject.SetActive(false);

        start.onClick.AddListener(() =>
        {
            canvasObject.GetComponent<GuaScreen>().Value = "loading";
            titleScreen.gameObject.SetActive(false);
            loadingScreen.gameObject.SetActive(true);
        });
        settings.onClick.AddListener(() => { });
        if (string.Equals(Environment.GetEnvironmentVariable("GUA_UNITY_HOST_CLICK"), "1", StringComparison.Ordinal))
            settings.gameObject.AddComponent<GuaUnityHostClickDriver>().Button = settings;

        BuildInactiveCoverageControls(canvasObject.transform);
        BuildWorldObjects();
    }

    private static void BuildWorldObjects()
    {
        AddWorldObject("Player", "player-world", "actor", GuaWorldSpace.World2D, new Vector3(635, 180, 0));
        var doorA = AddWorldObject("Door A", "door-a", "door", GuaWorldSpace.World2D, new Vector3(640, 180, 0));
        doorA.SetState("locked", true);
        var doorB = AddWorldObject("Door B", "door-b", "door", GuaWorldSpace.World2D, new Vector3(635, 185, 0));
        doorB.SetState("locked", true);
        AddWorldObject("3D Anchor", "anchor-3d", "anchor", GuaWorldSpace.World3D, Vector3.zero);
        AddWorldObject("3D Target", "target-3d", "target", GuaWorldSpace.World3D, new Vector3(1, 2, 2));
    }

    private static GuaUnityWorldObject AddWorldObject(string name, string id, string kind, GuaWorldSpace space, Vector3 position)
    {
        var objectGameObject = new GameObject(name);
        objectGameObject.transform.position = position;
        var worldObject = objectGameObject.AddComponent<GuaUnityWorldObject>();
        worldObject.Id = id;
        worldObject.Kind = kind;
        worldObject.Space = space;
        worldObject.VisibleToPlayer = true;
        return worldObject;
    }

    private static void BuildInactiveCoverageControls(Transform parent)
    {
        var coverage = new GameObject("InactiveCoverage", typeof(RectTransform));
        coverage.transform.SetParent(parent, false);

        var inputObject = new GameObject("NameInput", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(InputField), typeof(GuaId));
        inputObject.transform.SetParent(coverage.transform, false);
        inputObject.GetComponent<GuaId>().Value = "sample-input";
        var inputRect = inputObject.GetComponent<RectTransform>();
        inputRect.anchoredPosition = new Vector2(-420, 260);
        inputRect.sizeDelta = new Vector2(220, 44);
        var inputText = Text("Text", inputObject.transform, "pilot", Vector2.zero, inputRect.sizeDelta, 16);
        Stretch(inputText.rectTransform);
        var input = inputObject.GetComponent<InputField>();
        input.textComponent = inputText;
        input.text = "pilot";
        input.caretPosition = input.text.Length;

        var sensitiveInputObject = new GameObject("SensitiveInput", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(InputField), typeof(GuaId));
        sensitiveInputObject.transform.SetParent(coverage.transform, false);
        sensitiveInputObject.GetComponent<GuaId>().Value = "sensitive-input";
        var sensitiveInputRect = sensitiveInputObject.GetComponent<RectTransform>();
        sensitiveInputRect.anchoredPosition = new Vector2(-420, 320);
        sensitiveInputRect.sizeDelta = new Vector2(220, 44);
        var sensitiveInputText = Text("Text", sensitiveInputObject.transform, "private", Vector2.zero, sensitiveInputRect.sizeDelta, 16);
        Stretch(sensitiveInputText.rectTransform);
        var sensitiveInput = sensitiveInputObject.GetComponent<InputField>();
        sensitiveInput.textComponent = sensitiveInputText;
        sensitiveInput.text = "private";
        sensitiveInput.caretPosition = sensitiveInput.text.Length;

        var throwingInputObject = new GameObject("ThrowingInput", typeof(RectTransform), typeof(GuaId), typeof(GuaUnityThrowingControl));
        throwingInputObject.transform.SetParent(coverage.transform, false);
        throwingInputObject.GetComponent<GuaId>().Value = "throwing-input";

        var sliderRect = Rect("SampleSlider", coverage.transform, new Vector2(-420, 200), new Vector2(220, 32));
        sliderRect.gameObject.AddComponent<GuaId>().Value = "sample-slider";
        var slider = sliderRect.gameObject.AddComponent<UnityEngine.UI.Slider>();
        slider.minValue = 0;
        slider.maxValue = 10;
        slider.value = 5;

        var tmpButtonRect = Rect("TmpButton", coverage.transform, new Vector2(-420, 140), new Vector2(220, 44));
        tmpButtonRect.gameObject.AddComponent<UnityEngine.UI.Image>().color = ButtonNormal;
        tmpButtonRect.gameObject.AddComponent<GuaId>().Value = "tmp-button";
        tmpButtonRect.gameObject.AddComponent<UnityEngine.UI.Button>();
        var tmpButtonLabel = Title("TmpButtonLabel", tmpButtonRect, "TMP Launch", Vector2.zero, tmpButtonRect.sizeDelta, 18);
        Stretch(tmpButtonLabel.rectTransform);

        var tmpInputRect = Rect("TmpInput", coverage.transform, new Vector2(-420, 80), new Vector2(220, 44));
        tmpInputRect.gameObject.AddComponent<UnityEngine.UI.Image>().color = Color.white;
        tmpInputRect.gameObject.AddComponent<GuaId>().Value = "tmp-input";
        var tmpInput = tmpInputRect.gameObject.AddComponent<TMP_InputField>();
        var tmpViewport = Rect("Text Area", tmpInputRect, Vector2.zero, tmpInputRect.sizeDelta);
        Stretch(tmpViewport);
        var tmpInputText = Title("Text", tmpViewport, "bravo", Vector2.zero, tmpInputRect.sizeDelta, 16);
        Stretch(tmpInputText.rectTransform);
        tmpInput.textViewport = tmpViewport;
        tmpInput.textComponent = tmpInputText;
        tmpInput.text = "bravo";
        tmpInput.caretPosition = tmpInput.text.Length;

        var documentObject = new GameObject("ToolkitDocument", typeof(UIDocument));
        documentObject.transform.SetParent(coverage.transform, false);
        var document = documentObject.GetComponent<UIDocument>();
        document.panelSettings = Resources.Load<PanelSettings>("GuaFixturePanelSettings");
        var root = document.rootVisualElement;
        root.name = "toolkit-root";
        root.style.width = 640;
        root.style.height = 360;
        if (Environment.GetEnvironmentVariable("GUA_UNITY_TOOLKIT_CLICKS") == "1")
        {
            document.panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            document.panelSettings.scale = float.Parse(Environment.GetEnvironmentVariable("GUA_UNITY_TOOLKIT_SCALE") ?? "1",
                System.Globalization.CultureInfo.InvariantCulture);
            BuildToolkitClickControls(root);
            return;
        }
        var scaledBox = new VisualElement { name = "scaled-box" };
        scaledBox.style.position = Position.Absolute;
        scaledBox.style.left = 100;
        scaledBox.style.top = 60;
        scaledBox.style.width = 200;
        scaledBox.style.height = 40;
        root.Add(scaledBox);
        var list = new ListView { name = "fixture-list", itemsSource = new List<string> { "One", "Two", "Three" }, fixedItemHeight = 24 };
        list.makeItem = () => new UnityEngine.UIElements.Label();
        list.bindItem = (element, index) => ((UnityEngine.UIElements.Label)element).text = (string)list.itemsSource[index];
        list.style.position = Position.Absolute;
        list.style.left = 100;
        list.style.top = 130;
        list.style.width = 200;
        list.style.height = 80;
        root.Add(list);

        var integerSlider = new SliderInt("integer-slider", 0, 10) { name = "integer-slider", value = 3 };
        root.Add(integerSlider);

        var detachedSensitiveInput = new TextField("detached-sensitive-input") { name = "detached-sensitive-input", value = "attached" };
        root.Add(detachedSensitiveInput);
        documentObject.AddComponent<GuaUnityToolkitDetachDriver>().Configure(root, detachedSensitiveInput);

        var tabView = new TabView { name = "fixture-tabs" };
        tabView.Add(new Tab("First") { name = "first-tab" });
        tabView.Add(new Tab("Second") { name = "second-tab" });
        root.Add(tabView);

        var firstBranch = new VisualElement { name = "first-branch" };
        firstBranch.Add(new UnityEngine.UIElements.Button { name = "duplicate-button", text = "Duplicate A" });
        root.Add(firstBranch);
        var secondBranch = new VisualElement { name = "second-branch" };
        secondBranch.Add(new UnityEngine.UIElements.Button { name = "duplicate-button", text = "Duplicate B" });
        root.Add(secondBranch);

        var disabledCanvasObject = new GameObject("DisabledCanvas", typeof(RectTransform), typeof(Canvas));
        disabledCanvasObject.transform.SetParent(coverage.transform, false);
        disabledCanvasObject.GetComponent<Canvas>().enabled = false;
        Button("DisabledCanvasButton", "disabled-canvas-button", disabledCanvasObject.transform, "Hidden by Canvas", Vector2.zero);

        coverage.SetActive(string.Equals(Environment.GetEnvironmentVariable("GUA_UNITY_COVERAGE"), "1", StringComparison.Ordinal));
    }

    private static void BuildToolkitClickControls(VisualElement root)
    {
        root.style.width = 400;
        var status = new UnityEngine.UIElements.Label("clicks:0 down:0 up:0 events:0 count:0 centered:False") { name = "click-status" };
        root.Add(status);
        var clicks = 0; var downs = 0; var ups = 0; var events = 0; var count = 0; var centered = false;
        void UpdateStatus() => status.text = $"clicks:{clicks} down:{downs} up:{ups} events:{events} count:{count} centered:{centered}";
        var button = new UnityEngine.UIElements.Button(() => { clicks++; UpdateStatus(); }) { name = "click-button", text = "Click Target" };
        button.style.height = 36;
        button.RegisterCallback<PointerDownEvent>(_ => { downs++; UpdateStatus(); }, TrickleDown.TrickleDown);
        button.RegisterCallback<PointerUpEvent>(_ => { ups++; UpdateStatus(); }, TrickleDown.TrickleDown);
        button.RegisterCallback<ClickEvent>(evt =>
        {
            events++; count = evt.clickCount;
            centered = Vector2.Distance(evt.position, button.worldBound.center) < 1;
            UpdateStatus();
        });
        root.Add(button);
        // Pick may return a child instead of the button itself.
        var child = new VisualElement();
        child.style.position = Position.Absolute;
        child.style.left = child.style.right = child.style.top = child.style.bottom = 0;
        button.Add(child);
        var cover = new VisualElement { name = "click-cover" };
        cover.style.position = Position.Absolute;
        cover.style.left = cover.style.right = cover.style.top = cover.style.bottom = 0;
        cover.style.display = DisplayStyle.None;
        root.Add(new UnityEngine.UIElements.Button(() => { button.parent.Add(cover); cover.style.display = DisplayStyle.Flex; })
            { text = "Show Cover" });
        var scrollStatus = new UnityEngine.UIElements.Label("scrolled:0");
        root.Add(scrollStatus);
        var scroll = new ScrollView();
        scroll.style.height = 70;
        root.Add(scroll);
        var spacer = new VisualElement(); spacer.style.height = 300; spacer.style.flexShrink = 0;
        scroll.Add(spacer);
        var scrolled = 0;
        var scrollButton = new UnityEngine.UIElements.Button(() => scrollStatus.text = $"scrolled:{++scrolled}") { text = "Scroll Target" };
        scrollButton.style.height = 32; scrollButton.style.flexShrink = 0;
        scroll.Add(scrollButton);
        var tabs = new TabView();
        var first = new Tab("Click First") { name = "click-first-tab" };
        var second = new Tab("Click Second") { name = "click-second-tab" };
        first.Add(new UnityEngine.UIElements.Label("first content"));
        second.Add(new UnityEngine.UIElements.Label("second content"));
        tabs.Add(first); tabs.Add(second);
        root.Add(tabs);
        var tabStatus = new UnityEngine.UIElements.Label("tab:0");
        root.Add(tabStatus);
        tabs.activeTabChanged += (_, next) => tabStatus.text = next == second ? "tab:1" : "tab:0";
    }

    private static UnityEngine.UI.Button Button(string name, string id, Transform parent, string label, Vector2 position)
    {
        var rect = Rect(name, parent, position, new Vector2(256, 56));
        rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = Color.white;
        rect.gameObject.AddComponent<GuaId>().Value = id;
        var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
        var colors = button.colors;
        colors.normalColor = ButtonNormal;
        colors.highlightedColor = ButtonHighlighted;
        colors.pressedColor = new Color(0.11f, 0.11f, 0.11f, 1f);
        colors.selectedColor = ButtonHighlighted;
        button.colors = colors;

        var labelText = Text("Label", rect, label, Vector2.zero, new Vector2(256, 56), 18);
        Stretch(labelText.rectTransform);
        return button;
    }

    private static TextMeshProUGUI Title(string name, Transform parent, string value, Vector2 position, Vector2 size, float fontSize)
    {
        var rect = Rect(name, parent, position, size);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        return text;
    }

    private static Text Text(string name, Transform parent, string value, Vector2 position, Vector2 size, int fontSize)
    {
        var rect = Rect(name, parent, position, size);
        var text = rect.gameObject.AddComponent<Text>();
        text.text = value;
        text.font = legacyFont;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        return text;
    }

    private static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));
        var rect = gameObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}

public sealed class GuaUnityThrowingControl : MonoBehaviour
{
    public string Value { get; set; } = "unchanged";
}

public sealed class GuaUnityThrowingAdapter : IGuaUnityControlAdapter
{
    private sealed class Target
    {
        public Target(GuaUnityThrowingControl control) => Control = control;

        public GuaUnityThrowingControl Control { get; }
    }

    public bool TryDescribe(Transform transform, out object target, out string role, out string label, out string value)
    {
        var control = transform.GetComponent<GuaUnityThrowingControl>();
        if (control != null)
        {
            target = new Target(control);
            role = "textbox";
            label = "throwing-input";
            value = control.Value;
            return true;
        }
        target = null;
        role = null;
        label = null;
        value = null;
        return false;
    }

    public bool TryApply(object target, GuaActionRequest request, out string value)
    {
        value = null;
        if (target is not Target wrapper || request.Action != GuaActionType.SetValue) return false;
        wrapper.Control.Value = request.Value ?? string.Empty;
        throw new InvalidOperationException("Rejected sensitive value: " + request.Value);
    }
}

public sealed class GuaUnityHostClickDriver : MonoBehaviour
{
    public UnityEngine.UI.Button Button { get; set; }

    private IEnumerator Start()
    {
        yield return null;
        yield return null;
        Button.onClick.Invoke();
        Destroy(this);
    }
}

public sealed class GuaUnityToolkitDetachDriver : MonoBehaviour
{
    private VisualElement parent;
    private TextField target;
    private string initialValue;
    private int detachedFrames = -1;

    public void Configure(VisualElement targetParent, TextField targetField)
    {
        parent = targetParent;
        target = targetField;
        initialValue = targetField.value;
    }

    private void Update()
    {
        if (target == null || parent == null) return;
        if (detachedFrames < 0)
        {
            if (string.Equals(target.value, initialValue, StringComparison.Ordinal)) return;
            target.RemoveFromHierarchy();
            detachedFrames = 10;
            return;
        }
        if (detachedFrames-- > 0) return;
        parent.Add(target);
        Destroy(this);
    }
}
