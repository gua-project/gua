using TMPro;
using Gua.Core;
using Gua.Unity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using GuaUnityWorldObject = Gua.Unity.GuaWorldObject;

public static class GuaRuntimeUiSample
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Build()
    {
        if (Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var events = new GameObject("EventSystem");
            events.AddComponent<EventSystem>();
            events.AddComponent<StandaloneInputModule>();
        }
        var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var buttonObject = new GameObject("StartButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
        buttonObject.transform.SetParent(canvasObject.transform, false);
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        labelObject.GetComponent<Text>().text = "Start Game";
        var tmpObject = new GameObject("TmpLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        tmpObject.transform.SetParent(canvasObject.transform, false);
        tmpObject.GetComponent<TextMeshProUGUI>().text = "TMP Ready";

        var documentObject = new GameObject("ToolkitDocument", typeof(UIDocument));
        var document = documentObject.GetComponent<UIDocument>();
        document.rootVisualElement.Add(new UnityEngine.UIElements.Label("Toolkit Ready") { name = "toolkit-label" });
        var clickCount = 0;
        var clickStatus = new UnityEngine.UIElements.Label("Toolkit clicks: 0") { name = "toolkit-click-status" };
        document.rootVisualElement.Add(new UnityEngine.UIElements.Button(() => clickStatus.text = $"Toolkit clicks: {++clickCount}")
            { name = "toolkit-button", text = "Toolkit Start" });
        document.rootVisualElement.Add(clickStatus);
        document.rootVisualElement.Add(new TextField("Callsign") { name = "toolkit-input", value = "alpha" });

        var doorObject = new GameObject("Door A");
        doorObject.transform.position = new Vector3(640, 180, 0);
        var door = doorObject.AddComponent<GuaUnityWorldObject>();
        door.Id = "door-a";
        door.Kind = "door";
        door.Label = "Door A";
        door.Space = GuaWorldSpace.World2D;
        door.VisibleToPlayer = true;
        door.Tags = new[] { "east-corridor", "mission-critical" };
        door.SetState("open", false);
        door.SetState("locked", true);

        AddWorldObject("Player", "player-world", "actor", GuaWorldSpace.World2D, new Vector3(635, 180, 0));
        var tiedDoor = AddWorldObject("Door B", "door-b", "door", GuaWorldSpace.World2D, new Vector3(635, 185, 0));
        tiedDoor.SetState("locked", true);
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
}
