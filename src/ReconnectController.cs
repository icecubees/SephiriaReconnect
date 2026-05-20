using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HeathenEngineering.SteamworksIntegration;
using HeathenEngineering.SteamworksIntegration.API;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SephiriaReconnect;

public sealed class ReconnectController : MonoBehaviour
{
    public const string ProtocolVersion = "sephiria-reconnect/0.1";
    public static ReconnectController Instance { get; private set; }

    private ReconnectStore store;
    private ReconnectConfig config;
    private ReconnectSessionRecord hostSession;
    private LastSessionRecord lastSession;

    private bool panelOpen;
    private Canvas uiCanvas;
    private GameObject iconObject;
    private Image iconImage;
    private Text iconText;
    private Sprite iconSprite;
    private Sprite iconHoverSprite;
    private Sprite iconOfflineSprite;
    private Sprite iconDisabledSprite;
    private GameObject panelObject;
    private Image panelImage;
    private Text panelText;
    private RectTransform iconRectTransform;
    private RectTransform panelRectTransform;
    private readonly List<PanelActionButton> panelButtons = new List<PanelActionButton>();
    private Font uiFont;
    private UI_HUDMenu cachedHudMenu;
    private bool gameStyleApplied;
    private bool iconHovered;
    private float nextHelloAt;
    private float nextProbeAt;
    private float nextHudVisibilityProbeAt;
    private float nextSaveHostSessionAt;
    private float nextIconPlacementProbeAt;
    private float nextHostSessionRefreshAt;
    private float nextHostPlayerRefreshAt;
    private float nextHostLobbyRefreshAt;
    private float nextHostLobbyPublishAt;
    private bool lastHudVisible;
    private string lastPublishedLobbyState = "";
    private string lastSavedHostSessionState = "";
    private int autoHelloAttempts;
    private bool clientHelloAccepted;
    private bool wasServerActive;
    private bool serverEventsAttached;
    private bool messageHandlersRegistered;
    private bool wasClientActive;
    private string lastObservedFloorGuid = "";
    private string statusLine = "正在初始化。";
    private LobbySettingsSnapshot pendingLobbySettings;
    private float reconnectConnectionOpenUntil;
    private bool reconnectJoinWindowOpen;
    private bool reconnectPreviousAllowConnection;
    private bool reconnectPreviousAccessDenyInDungeon;
    private static readonly Vector3[] ScreenRectCorners = new Vector3[4];
    private static readonly MethodInfo DungeonSaveCurrentSessionDataMethod =
        typeof(DungeonManager).GetMethod("SaveCurrentSessionData", BindingFlags.Instance | BindingFlags.NonPublic);

    private void Awake()
    {
        if (Instance != null && !ReferenceEquals(Instance, this))
        {
            Debug.LogWarning("[SephiriaReconnect] Duplicate controller detected, destroying the new instance.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        string folder = Path.GetDirectoryName(typeof(ReconnectMod).Assembly.Location);
        if (string.IsNullOrEmpty(folder))
        {
            folder = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "AddOns", "SephiriaReconnect");
        }

        store = new ReconnectStore(folder);
        ReconnectLogger.Initialize(folder, new ReconnectConfig());
        config = store.LoadConfig();
        ReconnectLogger.Configure(config);
        store.SaveConfig(config);
        hostSession = store.LoadHostSession();
        lastSession = store.LoadLastSession();
        RegisterMessageHandlers();
        if (ShouldShowReconnectUi())
        {
            EnsureUi();
        }
    }

    private void OnDestroy()
    {
        bool wasActiveInstance = ReferenceEquals(Instance, this);
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }

        DetachServerEvents();
        UnregisterMessageHandlers();
        DestroyUi();
        if (wasActiveInstance)
        {
            ReconnectLogger.Shutdown();
        }
    }

    private void Update()
    {
        bool shouldShowUi = ShouldShowReconnectUi();
        if (shouldShowUi)
        {
            EnsureUi();
        }
        else if (uiCanvas != null)
        {
            DestroyUi();
        }

        if (uiCanvas != null)
        {
            UpdateHudVisibility();
        }

        if (Time.unscaledTime < nextProbeAt)
        {
            return;
        }

        nextProbeAt = Time.unscaledTime + 1f;
        RegisterMessageHandlers();
        TrackConnectionDrop();
        TrackHostSession();
        TrackClientHello();
        if (uiCanvas != null)
        {
            AutoPlaceIconAfterLowerLeftHud();
            RefreshUi();
        }
    }

    private static bool ShouldShowReconnectUi()
    {
        return NetworkServer.active || NetworkClient.active;
    }

    private void UpdateHudVisibility()
    {
        if (Time.unscaledTime >= nextHudVisibilityProbeAt)
        {
            nextHudVisibilityProbeAt = Time.unscaledTime + 0.2f;
            lastHudVisible = ShouldShowWithGameHud();
        }

        bool visible = lastHudVisible;
        if (iconObject != null && iconObject.activeSelf != visible)
        {
            iconObject.SetActive(visible);
        }

        if (!visible && panelOpen)
        {
            panelOpen = false;
            if (panelObject != null)
            {
                panelObject.SetActive(false);
            }
        }
    }

    private bool ShouldShowWithGameHud()
    {
        if (!ShouldShowReconnectUi())
        {
            return false;
        }

        try
        {
            if (UIManager.Instance != null)
            {
                if (UIManager.Instance.IsHidden)
                {
                    return false;
                }

                if (UIManager.Instance.CurrentControlStack != null && UIManager.Instance.CurrentControlStack.Count > 0)
                {
                    return false;
                }
            }

            if (ScreenFader.Instance != null && ScreenFader.Instance.IsFading)
            {
                return false;
            }

            PlayerSpawner localSpawner = GetLocalSpawner();
            if (localSpawner != null && localSpawner.PlayerAvatar != null && localSpawner.PlayerAvatar.loadingScreenType != -1)
            {
                return false;
            }

            UI_HUDMenu hudMenu = GetCachedHudMenu();
            if (hudMenu == null || !hudMenu.gameObject.activeInHierarchy)
            {
                return false;
            }

            CanvasGroup group = hudMenu.canvasGroup != null ? hudMenu.canvasGroup : hudMenu.CanvasGroup;
            if (group != null && group.alpha <= 0.05f)
            {
                return false;
            }

            return IsAnyHudMenuButtonVisible(hudMenu);
        }
        catch
        {
            return false;
        }
    }

    private UI_HUDMenu GetCachedHudMenu()
    {
        if (cachedHudMenu == null)
        {
            cachedHudMenu = FindFirstObjectByType<UI_HUDMenu>(FindObjectsInactive.Exclude);
            UpdateUiCanvasSortingOrder();
        }

        return cachedHudMenu;
    }

    private void UpdateUiCanvasSortingOrder()
    {
        if (uiCanvas == null || cachedHudMenu == null)
        {
            return;
        }

        Canvas hudCanvas = cachedHudMenu.GetComponentInParent<Canvas>();
        if (hudCanvas == null || hudCanvas == uiCanvas)
        {
            return;
        }

        uiCanvas.sortingOrder = hudCanvas.sortingOrder + 1;
    }

    private static bool IsAnyHudMenuButtonVisible(UI_HUDMenu hudMenu)
    {
        return IsHudButtonVisible(hudMenu.inventoryButton)
            || IsHudButtonVisible(hudMenu.statsButton)
            || IsHudButtonVisible(hudMenu.passiveButton)
            || IsHudButtonVisible(hudMenu.presetButton);
    }

    private static bool IsHudButtonVisible(UI_HorayButton button)
    {
        return button != null && button.gameObject.activeInHierarchy;
    }

    private void EnsureUi()
    {
        if (uiCanvas != null)
        {
            return;
        }

        DestroyDuplicateUiCanvases();
        GameObject canvasObject = new GameObject("SephiriaReconnectCanvas");
        DontDestroyOnLoad(canvasObject);
        uiCanvas = canvasObject.AddComponent<Canvas>();
        uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        uiCanvas.sortingOrder = 100;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;
        canvasObject.AddComponent<GraphicRaycaster>();
        UpdateUiCanvasSortingOrder();
        float iconSize = GetIconSize();
        iconObject = CreateVisualButton(canvasObject.transform, "ReconnectIcon", "", new Vector2(iconSize, iconSize));
        ClickRelay clickRelay = iconObject.AddComponent<ClickRelay>();
        clickRelay.Initialize(TogglePanel);
        clickRelay.SetHoverAction(SetIconHover);
        iconImage = iconObject.GetComponent<Image>();
        iconText = iconObject.GetComponentInChildren<Text>();
        if (iconText != null)
        {
            iconText.gameObject.SetActive(false);
        }

        LoadReconnectIconSprites();
        ApplyReconnectIconVisual();
        iconRectTransform = iconObject.GetComponent<RectTransform>();
        iconRectTransform.anchorMin = Vector2.zero;
        iconRectTransform.anchorMax = Vector2.zero;
        iconRectTransform.pivot = new Vector2(0f, 0f);
        SetIconPosition(new Vector2(config.IconOffsetX, config.IconOffsetY));

        panelObject = new GameObject("ReconnectPanel");
        panelObject.transform.SetParent(canvasObject.transform, false);
        panelImage = panelObject.AddComponent<Image>();
        panelImage.raycastTarget = false;
        panelImage.color = new Color(0.05f, 0.055f, 0.06f, 0.94f);
        panelRectTransform = panelObject.GetComponent<RectTransform>();
        panelRectTransform.anchorMin = Vector2.zero;
        panelRectTransform.anchorMax = Vector2.zero;
        panelRectTransform.pivot = new Vector2(0f, 0f);
        panelRectTransform.sizeDelta = new Vector2(460f, 360f);
        panelRectTransform.anchoredPosition = new Vector2(config.IconOffsetX, config.IconOffsetY + iconSize + 10f);

        panelText = CreateText(panelObject.transform, "ReconnectPanelText", 13, TextAnchor.UpperLeft);
        RectTransform textRect = panelText.GetComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(16f, 16f);
        textRect.offsetMax = new Vector2(-16f, -98f);

        AddPanelButton("本层恢复", new Vector2(16f, -16f), new Vector2(132f, 34f), PrepareRestoreCurrentCheckpoint);
        AddPanelButton("邀请重连", new Vector2(160f, -16f), new Vector2(140f, 34f), OpenSteamReconnectInvite);
        AddPanelButton("发送握手", new Vector2(312f, -16f), new Vector2(132f, 34f), SendHelloButton);
        AddPanelButton("加入上次房间", new Vector2(16f, -56f), new Vector2(140f, 34f), JoinLastLobby);

        panelObject.SetActive(false);
        TryApplyGameUiStyle();
        RefreshUi();
    }

    private void DestroyUi()
    {
        if (iconObject != null)
        {
            Destroy(iconObject);
        }

        if (uiCanvas != null)
        {
            Destroy(uiCanvas.gameObject);
        }

        uiCanvas = null;
        iconObject = null;
        iconImage = null;
        iconText = null;
        iconSprite = null;
        iconHoverSprite = null;
        iconOfflineSprite = null;
        iconDisabledSprite = null;
        iconHovered = false;
        iconRectTransform = null;
        panelRectTransform = null;
        panelObject = null;
        panelImage = null;
        panelText = null;
        panelButtons.Clear();
        gameStyleApplied = false;
        cachedHudMenu = null;
        lastHudVisible = false;
    }

    private void DestroyDuplicateUiCanvases()
    {
        GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < allObjects.Length; i++)
        {
            GameObject obj = allObjects[i];
            if (obj == null || obj == uiCanvas?.gameObject || obj.name != "SephiriaReconnectCanvas")
            {
                continue;
            }

            Destroy(obj);
        }
    }

    private void TogglePanel()
    {
        panelOpen = !panelOpen;
        if (panelObject != null)
        {
            panelObject.SetActive(panelOpen);
        }
        RefreshUi();
    }

    private void SetIconPosition(Vector2 position)
    {
        if (iconRectTransform != null)
        {
            iconRectTransform.anchoredPosition = position;
        }

        if (panelRectTransform != null)
        {
            panelRectTransform.anchoredPosition = new Vector2(position.x, position.y + GetIconSize() + 10f);
        }
    }

    private void AutoPlaceIconAfterLowerLeftHud()
    {
        if (!config.AutoPlaceIconAfterLowerLeftHud || iconRectTransform == null || Time.unscaledTime < nextIconPlacementProbeAt)
        {
            return;
        }

        nextIconPlacementProbeAt = Time.unscaledTime + 1f;
        if (!TryAttachIconAfterLowerLeftHudCluster())
        {
            SetIconPosition(new Vector2(config.IconOffsetX, config.IconOffsetY));
        }
    }

    private bool TryAttachIconAfterLowerLeftHudCluster()
    {
        bool found = false;
        Rect unionRect = default;

        TryIncludeHudMenuButtonsInCluster(ref unionRect, ref found);
        if (found)
        {
            return TryPlaceIconAfterScreenRect(unionRect);
        }

        TryIncludeLowerLeftGraphicsInCluster(ref unionRect, ref found);
        return found && TryPlaceIconAfterScreenRect(unionRect);
    }

    private void TryIncludeHudMenuButtonsInCluster(ref Rect unionRect, ref bool found)
    {
        UI_HUDMenu hudMenu = GetCachedHudMenu();
        if (hudMenu == null)
        {
            return;
        }

        TryIncludeHudMenuButton(hudMenu.inventoryButton, ref unionRect, ref found);
        TryIncludeHudMenuButton(hudMenu.statsButton, ref unionRect, ref found);
        TryIncludeHudMenuButton(hudMenu.passiveButton, ref unionRect, ref found);
        TryIncludeHudMenuButton(hudMenu.presetButton, ref unionRect, ref found);
    }

    private void TryIncludeHudMenuButton(UI_HorayButton button, ref Rect unionRect, ref bool found)
    {
        if (button == null || !button.gameObject.activeInHierarchy)
        {
            return;
        }

        RectTransform rect = button.transform as RectTransform;
        Canvas canvas = button.GetComponentInParent<Canvas>();
        if (rect == null || canvas == null)
        {
            return;
        }

        rect = FindLowerLeftHudIconContainer(rect, canvas) ?? rect;
        if (TryGetScreenRect(rect, canvas, out Rect screenRect))
        {
            IncludeLowerLeftRect(ref unionRect, ref found, screenRect);
        }
    }

    private void TryIncludeLowerLeftGraphicsInCluster(ref Rect unionRect, ref bool found)
    {
        Graphic[] graphics = FindObjectsByType<Graphic>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (Graphic graphic in graphics)
        {
            if (graphic == null || graphic.canvas == null || graphic.canvas == uiCanvas || !graphic.raycastTarget && graphic.GetComponent<Button>() == null)
            {
                continue;
            }

            RectTransform rect = FindLowerLeftHudIconContainer(graphic.rectTransform, graphic.canvas);
            if (rect == null || !rect.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!TryGetScreenRect(rect, graphic.canvas, out Rect screenRect))
            {
                continue;
            }

            if (!LooksLikeLowerLeftHudIcon(screenRect))
            {
                continue;
            }

            IncludeLowerLeftRect(ref unionRect, ref found, screenRect);
        }
    }

    private static void IncludeLowerLeftRect(ref Rect unionRect, ref bool found, Rect screenRect)
    {
        if (!LooksLikeLowerLeftHudIcon(screenRect))
        {
            return;
        }

        unionRect = found ? Rect.MinMaxRect(
            Mathf.Min(unionRect.xMin, screenRect.xMin),
            Mathf.Min(unionRect.yMin, screenRect.yMin),
            Mathf.Max(unionRect.xMax, screenRect.xMax),
            Mathf.Max(unionRect.yMax, screenRect.yMax)) : screenRect;
        found = true;
    }

    private bool TryPlaceIconAfterScreenRect(Rect lowerLeftHudRect)
    {
        if (uiCanvas == null || iconRectTransform == null)
        {
            return false;
        }

        if (iconRectTransform.parent != uiCanvas.transform)
        {
            iconRectTransform.SetParent(uiCanvas.transform, worldPositionStays: false);
        }

        iconRectTransform.anchorMin = Vector2.zero;
        iconRectTransform.anchorMax = Vector2.zero;
        iconRectTransform.pivot = new Vector2(0f, 0f);
        iconRectTransform.localScale = Vector3.one;
        iconRectTransform.localRotation = Quaternion.identity;
        float iconSize = GetIconSizeForHud(lowerLeftHudRect);
        iconRectTransform.sizeDelta = new Vector2(iconSize, iconSize);
        iconRectTransform.SetAsLastSibling();

        float desiredX = Mathf.Clamp(lowerLeftHudRect.xMax + config.IconGap, 4f, Screen.width - iconRectTransform.sizeDelta.x - 4f);
        float desiredY = Mathf.Clamp(lowerLeftHudRect.yMin, 4f, Screen.height - iconRectTransform.sizeDelta.y - 4f);
        iconRectTransform.anchoredPosition = new Vector2(desiredX, desiredY);

        if (panelRectTransform != null)
        {
            panelRectTransform.anchoredPosition = new Vector2(desiredX, desiredY + iconRectTransform.sizeDelta.y + 10f);
        }

        return true;
    }

    private static bool TryGetScreenRect(RectTransform rect, Canvas canvas, out Rect screenRect)
    {
        screenRect = default;
        rect.GetWorldCorners(ScreenRectCorners);
        Camera camera = GetCanvasCamera(canvas);

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < ScreenRectCorners.Length; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, ScreenRectCorners[i]);
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        if (max.x <= min.x || max.y <= min.y)
        {
            return false;
        }

        screenRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        return true;
    }

    private static bool LooksLikeLowerLeftHudIcon(Rect rect)
    {
        float width = rect.width;
        float height = rect.height;
        if (width < 18f || width > 90f || height < 18f || height > 90f)
        {
            return false;
        }

        if (rect.xMin < -4f || rect.xMin > 360f)
        {
            return false;
        }

        if (rect.yMin < -4f || rect.yMin > 130f)
        {
            return false;
        }

        return true;
    }

    private static RectTransform FindLowerLeftHudIconContainer(RectTransform start, Canvas canvas)
    {
        if (start == null)
        {
            return null;
        }

        RectTransform best = null;
        RectTransform current = start;
        while (current != null && current.GetComponent<Canvas>() == null)
        {
            if (!TryGetScreenRect(current, canvas, out Rect rect))
            {
                break;
            }

            if (!LooksLikeLowerLeftHudIcon(rect))
            {
                break;
            }

            best = current;
            current = current.parent as RectTransform;
        }

        return best;
    }

    private static Camera GetCanvasCamera(Canvas canvas)
    {
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            return null;
        }

        return canvas.worldCamera;
    }

    private void PollIconClickFallback()
    {
    }

    private void RefreshUi()
    {
        if (iconImage == null)
        {
            return;
        }

        TryApplyGameUiStyle();

        ApplyReconnectIconVisual();

        if (panelText == null || !panelOpen)
        {
            return;
        }

        LobbyInfo lobby = GetLobbyInfo();
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("状态：" + statusLine);
        builder.AppendLine();
        builder.AppendLine("身份：" + DescribeRole());
        builder.AppendLine("存档槽：" + GetSelectedSaveSlot());
        builder.AppendLine("局内标识：" + GetRunId());
        builder.AppendLine("网络：服务端=" + NetworkServer.active + "，客户端=" + NetworkClient.active);
        builder.AppendLine("房间：" + (lobby.HasLobby ? lobby.LobbyId.ToString() : "无"));
        builder.AppendLine("房主 SteamID：" + (lobby.HostSteamId == 0 ? "未知" : lobby.HostSteamId.ToString()));
        builder.AppendLine("日志：" + ShortPath(ReconnectLogger.CurrentLogPath));
        if (hostSession != null)
        {
            builder.AppendLine();
            builder.AppendLine("会话：" + hostSession.SessionId);
            builder.AppendLine("检查点：" + NullToDash(hostSession.CurrentCheckpointId));
            builder.AppendLine("检查点哈希：" + Short(hostSession.CurrentCheckpointHash));
            builder.AppendLine("队员：");
            foreach (ReconnectMemberRecord member in hostSession.Members.Values.OrderBy(m => m.PlayerSlot).ThenBy(m => m.SteamId))
            {
                builder.AppendLine("  槽位 " + member.PlayerSlot + "  Steam " + member.SteamId + "  " + DescribeMemberState(member.State) + "  " + DescribeMemberMod(member) + "  " + member.PlayerName);
            }
        }

        if (lastSession != null)
        {
            builder.AppendLine();
            builder.AppendLine("上次会话：" + lastSession.SessionId);
            builder.AppendLine("上次房间：" + lastSession.LobbyId);
            builder.AppendLine("上次检查点：" + NullToDash(lastSession.CheckpointId));
        }

        builder.AppendLine();
        builder.AppendLine("说明：当前版本执行“本楼层入口回滚”。");
        builder.AppendLine("流程：自动保存楼层入口点 -> 房主按历史 SteamID 发邀请 -> 掉线玩家回房 -> 房主执行本层恢复。");
        builder.AppendLine("本层恢复点只在当前会话内保留；进入新局会清理旧检查点。");

        panelText.text = builder.ToString();
    }

    private void AddPanelButton(string text, Vector2 anchoredPosition, Vector2 size, UnityEngine.Events.UnityAction action)
    {
        GameObject button = CreateVisualButton(panelObject.transform, text, text, size);
        button.AddComponent<ClickRelay>().Initialize(action);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        panelButtons.Add(new PanelActionButton
        {
            Rect = rect,
            Action = action,
            Image = button.GetComponent<Image>(),
            Label = button.GetComponentInChildren<Text>()
        });
    }

    private GameObject CreateVisualButton(Transform parent, string name, string text, Vector2 size)
    {
        GameObject buttonObject = new GameObject(name);
        buttonObject.transform.SetParent(parent, false);
        Image image = buttonObject.AddComponent<Image>();
        image.raycastTarget = true;
        image.color = new Color(0.15f, 0.17f, 0.19f, 0.95f);

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = size;

        Text label = CreateText(buttonObject.transform, name + "Text", 13, TextAnchor.MiddleCenter);
        label.text = text;
        label.color = Color.white;
        RectTransform labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        return buttonObject;
    }

    private Text CreateText(Transform parent, string name, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = new GameObject(name);
        textObject.transform.SetParent(parent, false);
        Text text = textObject.AddComponent<Text>();
        text.raycastTarget = false;
        text.font = GetUiFont();
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = new Color(0.92f, 0.94f, 0.96f, 1f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private Font GetUiFont()
    {
        if (uiFont != null)
        {
            return uiFont;
        }

        try
        {
            uiFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Source Han Sans CN", "Arial Unicode MS" },
                16);
        }
        catch
        {
            uiFont = null;
        }

        if (uiFont == null)
        {
            uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        return uiFont;
    }

    private void TryApplyGameUiStyle()
    {
        if (gameStyleApplied)
        {
            return;
        }

        bool appliedAny = false;
        UI_HUDMenu hudMenu = GetCachedHudMenu();
        UI_HorayButton styleButton = GetStyleButton(hudMenu);
        if (styleButton != null)
        {
            Image sourceImage = styleButton.targetGraphic as Image ?? styleButton.GetComponent<Image>();
            if (sourceImage != null)
            {
                if (iconImage != null)
                {
                    iconImage.raycastTarget = true;
                }

                for (int i = 0; i < panelButtons.Count; i++)
                {
                    if (panelButtons[i].Image != null)
                    {
                        CopyImageStyle(sourceImage, panelButtons[i].Image);
                        panelButtons[i].Image.raycastTarget = true;
                    }
                }

                appliedAny = true;
            }
        }

        Image sourcePanel = FindPanelStyleImage();
        if (sourcePanel != null && panelImage != null)
        {
            CopyImageStyle(sourcePanel, panelImage);
            Color color = sourcePanel.color;
            color.a = 0.94f;
            panelImage.color = color;
            panelImage.raycastTarget = false;
            appliedAny = true;
        }

        if (appliedAny)
        {
            ApplyTextEffects(iconText, 16);
            ApplyTextEffects(panelText, 13);
            for (int i = 0; i < panelButtons.Count; i++)
            {
                ApplyTextEffects(panelButtons[i].Label, 13);
            }

            gameStyleApplied = true;
        }
    }

    private static UI_HorayButton GetStyleButton(UI_HUDMenu hudMenu)
    {
        if (hudMenu == null)
        {
            return null;
        }

        return hudMenu.inventoryButton ?? hudMenu.statsButton ?? hudMenu.passiveButton ?? hudMenu.presetButton;
    }

    private static Image FindPanelStyleImage()
    {
        Image image = FindLargestImage(FindFirstObjectByType<UI_GameOverLabel>(FindObjectsInactive.Exclude));
        if (image != null)
        {
            return image;
        }

        image = FindLargestImage(FindFirstObjectByType<UI_CharacterStatusPanel>(FindObjectsInactive.Exclude));
        if (image != null)
        {
            return image;
        }

        return null;
    }

    private static Image FindLargestImage(Component root)
    {
        if (root == null)
        {
            return null;
        }

        Image best = null;
        float bestArea = 0f;
        Image[] images = root.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < images.Length; i++)
        {
            Image candidate = images[i];
            if (candidate == null || candidate.rectTransform == null || candidate.rectTransform.rect.width <= 0f || candidate.rectTransform.rect.height <= 0f)
            {
                continue;
            }

            float area = candidate.rectTransform.rect.width * candidate.rectTransform.rect.height;
            if (area > bestArea)
            {
                bestArea = area;
                best = candidate;
            }
        }

        return best;
    }

    private static void CopyImageStyle(Image source, Image target)
    {
        if (source == null || target == null)
        {
            return;
        }

        target.sprite = source.sprite;
        target.overrideSprite = source.overrideSprite;
        target.material = source.material;
        target.type = source.type;
        target.fillCenter = source.fillCenter;
        target.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        target.preserveAspect = source.preserveAspect;
    }

    private void SetIconHover(bool hover)
    {
        iconHovered = hover;
        ApplyReconnectIconVisual();
    }

    private void ApplyReconnectIconVisual()
    {
        if (iconImage == null)
        {
            return;
        }

        iconImage.sprite = SelectReconnectIconSprite();
        iconImage.type = Image.Type.Simple;
        iconImage.preserveAspect = true;
        iconImage.material = null;
        iconImage.color = Color.white;
        if (iconRectTransform != null)
        {
            iconRectTransform.localScale = iconHovered ? new Vector3(1.08f, 1.08f, 1f) : Vector3.one;
        }
    }

    private Sprite SelectReconnectIconSprite()
    {
        if (!NetworkClient.active && !NetworkServer.active)
        {
            return iconDisabledSprite != null ? iconDisabledSprite : iconSprite;
        }

        if (HasOfflineReservedMember() && iconOfflineSprite != null)
        {
            return iconOfflineSprite;
        }

        if (iconHovered && iconHoverSprite != null)
        {
            return iconHoverSprite;
        }

        return iconSprite;
    }

    private float GetIconSize()
    {
        if (config == null)
        {
            return 56f;
        }

        return Mathf.Clamp(config.IconSize <= 0f ? 56f : config.IconSize, 32f, 72f);
    }

    private float GetIconSizeForHud(Rect lowerLeftHudRect)
    {
        if (lowerLeftHudRect.height <= 0f)
        {
            return GetIconSize();
        }

        return Mathf.Clamp(lowerLeftHudRect.height, 32f, 72f);
    }

    private void LoadReconnectIconSprites()
    {
        iconSprite = LoadReconnectIconSprite("reconnect-rabbit-green.png") ?? CreateReconnectIconSprite(hover: false);
        iconHoverSprite = LoadReconnectIconSprite("reconnect-rabbit-green-hover.png") ?? CreateReconnectIconSprite(hover: true);
        iconOfflineSprite = LoadReconnectIconSprite("reconnect-rabbit-amber.png") ?? iconSprite;
        iconDisabledSprite = LoadReconnectIconSprite("reconnect-rabbit-gray.png") ?? iconSprite;
    }

    private Sprite LoadReconnectIconSprite(string fileName)
    {
        try
        {
            string folder = Path.GetDirectoryName(typeof(ReconnectMod).Assembly.Location);
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            string path = Path.Combine(folder, "assets", fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (!ImageConversion.LoadImage(texture, bytes, true))
            {
                Destroy(texture);
                return null;
            }

            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 1f);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to load icon asset " + fileName + ": " + ex.Message);
            return null;
        }
    }

    private static Sprite CreateReconnectIconSprite(bool hover)
    {
        const int width = 36;
        const int height = 36;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        texture.filterMode = FilterMode.Point;
        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 border = hover ? new Color32(10, 12, 24, 255) : new Color32(4, 4, 10, 255);
        Color32 shadow = new Color32(0, 0, 0, 180);
        Color32 body = hover ? new Color32(45, 55, 82, 255) : new Color32(28, 30, 48, 245);
        Color32 body2 = hover ? new Color32(76, 92, 126, 255) : new Color32(49, 54, 78, 245);
        Color32 cyan = hover ? new Color32(120, 236, 255, 255) : new Color32(82, 196, 224, 255);
        Color32 white = hover ? new Color32(255, 255, 255, 255) : new Color32(222, 235, 242, 255);
        Color32 glow = hover ? new Color32(62, 184, 255, 210) : new Color32(44, 130, 180, 150);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                texture.SetPixel(x, y, clear);
            }
        }

        FillRect(texture, 6, 4, 24, 28, shadow);
        FillRect(texture, 4, 3, 26, 29, border);
        FillRect(texture, 7, 6, 20, 23, body);
        FillRect(texture, 9, 8, 16, 19, body2);
        FillRect(texture, 7, 4, 20, 3, new Color32(82, 92, 116, 230));
        FillRect(texture, 6, 28, 22, 3, new Color32(12, 14, 24, 230));

        DrawLine(texture, 10, 18, 10, 14, cyan);
        DrawLine(texture, 10, 14, 15, 14, cyan);
        DrawLine(texture, 15, 14, 15, 11, cyan);
        DrawLine(texture, 21, 18, 21, 22, cyan);
        DrawLine(texture, 21, 22, 16, 22, cyan);
        DrawLine(texture, 16, 22, 16, 25, cyan);

        FillRect(texture, 8, 17, 5, 3, white);
        FillRect(texture, 19, 16, 5, 3, white);
        FillRect(texture, 14, 10, 4, 4, glow);
        FillRect(texture, 14, 22, 4, 4, glow);

        DrawLine(texture, 13, 18, 19, 18, white);
        DrawLine(texture, 18, 16, 21, 18, white);
        DrawLine(texture, 18, 20, 21, 18, white);
        DrawLine(texture, 18, 21, 12, 21, cyan);
        DrawLine(texture, 13, 19, 10, 21, cyan);
        DrawLine(texture, 13, 23, 10, 21, cyan);

        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 1f);
    }

    private static void FillRect(Texture2D texture, int x, int y, int width, int height, Color32 color)
    {
        for (int yy = y; yy < y + height; yy++)
        {
            for (int xx = x; xx < x + width; xx++)
            {
                if (xx >= 0 && xx < texture.width && yy >= 0 && yy < texture.height)
                {
                    texture.SetPixel(xx, yy, color);
                }
            }
        }
    }

    private static void DrawLine(Texture2D texture, int x0, int y0, int x1, int y1, Color32 color)
    {
        int dx = Mathf.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < texture.width && y0 >= 0 && y0 < texture.height)
            {
                texture.SetPixel(x0, y0, color);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void ApplyTextEffects(Text text, int fontSize)
    {
        if (text == null)
        {
            return;
        }

        text.fontSize = fontSize;
        text.color = new Color(0.96f, 0.97f, 0.98f, 1f);
        Outline outline = text.GetComponent<Outline>();
        if (outline == null)
        {
            outline = text.gameObject.AddComponent<Outline>();
        }

        outline.effectColor = new Color(0.08f, 0.09f, 0.1f, 0.9f);
        outline.effectDistance = new Vector2(1f, -1f);
    }

    private void SendHelloButton()
    {
        SendHello(reconnectAttempt: true);
    }

    private void OpenSteamReconnectInvite()
    {
        LobbyInfo lobby = GetLobbyInfo();
        if (NetworkServer.active)
        {
            OpenReconnectJoinWindow("invite");
        }

        if (!lobby.HasLobby || lobby.LobbyId == 0)
        {
            if (NetworkServer.active && lobby.Manager != null)
            {
                try
                {
                    lobby.Manager.Create();
                    StartCoroutine(OpenSteamReconnectInviteWhenLobbyReady());
                    statusLine = "正在创建 Steam 房间，随后打开邀请窗口。";
                    ShowSystemMessage(statusLine);
                }
                catch (Exception ex)
                {
                    statusLine = "创建 Steam 房间失败：" + ex.Message;
                    Debug.LogError("[SephiriaReconnect] Create lobby for invite failed: " + ex);
                }
                return;
            }

            statusLine = "当前没有可邀请的 Steam 房间。";
            return;
        }

        ApplyLobbySettingsSnapshot(lobby);
        OpenSteamInviteForLobby(lobby.LobbyId);
    }

    private IEnumerator OpenSteamReconnectInviteWhenLobbyReady()
    {
        float deadline = Time.unscaledTime + 8f;
        while (Time.unscaledTime < deadline)
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (lobby.HasLobby && lobby.LobbyId != 0)
            {
                ApplyLobbySettingsSnapshot(lobby);
                OpenSteamInviteForLobby(lobby.LobbyId);
                yield break;
            }

            yield return null;
        }

        statusLine = "创建 Steam 房间超时，无法打开邀请窗口。";
        ShowSystemMessage(statusLine);
    }

    private void OpenSteamInviteForLobby(ulong lobbyId)
    {
        try
        {
            OpenReconnectJoinWindow("open-invite");
            int sent = InviteRecordedDisconnectedPlayers(lobbyId);
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobbyId));
            statusLine = sent > 0 ? "已向历史掉线成员发送 Steam 邀请，并打开邀请窗口。" : "已打开 Steam 邀请窗口；没有可自动定向邀请的掉线成员。";
            ShowSystemMessage(statusLine);
        }
        catch (Exception ex)
        {
            statusLine = "打开 Steam 邀请窗口失败：" + ex.Message;
            Debug.LogError("[SephiriaReconnect] Open invite dialog failed: " + ex);
        }
    }

    private int InviteRecordedDisconnectedPlayers(ulong lobbyId)
    {
        if (hostSession == null || lobbyId == 0)
        {
            return 0;
        }

        int sent = 0;
        string connectString = "+connect_lobby " + lobbyId;
        foreach (ReconnectMemberRecord member in hostSession.Members.Values)
        {
            if (member == null || member.SteamId == 0 || member.SteamId == hostSession.HostSteamId)
            {
                continue;
            }

            if (member.State != ReconnectMemberState.OfflineReserved && member.State != ReconnectMemberState.ReconnectPending)
            {
                continue;
            }

            try
            {
                if (SteamFriends.InviteUserToGame(new CSteamID(member.SteamId), connectString))
                {
                    sent++;
                    member.State = ReconnectMemberState.ReconnectPending;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SephiriaReconnect] Failed to invite " + member.SteamId + ": " + ex.Message);
            }
        }

        if (sent > 0)
        {
            SaveHostSession();
        }

        return sent;
    }

    private bool HasOfflineReservedMember()
    {
        return hostSession != null && hostSession.Members.Values.Any(m => m.State == ReconnectMemberState.OfflineReserved || m.State == ReconnectMemberState.ReconnectedWaiting);
    }

    private bool HasUnreturnedReservedMember()
    {
        return hostSession != null && hostSession.Members.Values.Any(m => m.State == ReconnectMemberState.OfflineReserved || m.State == ReconnectMemberState.ReconnectPending);
    }

    private void RegisterMessageHandlers()
    {
        if (messageHandlersRegistered)
        {
            return;
        }

        try
        {
            NetworkServer.RegisterHandler<ReconnectHelloMessage>(HandleServerHello, false);
            NetworkClient.RegisterHandler<ReconnectHelloReplyMessage>(HandleClientHelloReply, false);
            messageHandlersRegistered = true;
            Debug.Log("[SephiriaReconnect] Mirror message handlers registered.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Message handler registration delayed: " + ex.Message);
        }
    }

    private void UnregisterMessageHandlers()
    {
        if (!messageHandlersRegistered)
        {
            return;
        }

        try
        {
            NetworkServer.UnregisterHandler<ReconnectHelloMessage>();
            NetworkClient.UnregisterHandler<ReconnectHelloReplyMessage>();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to unregister handlers: " + ex.Message);
        }

        messageHandlersRegistered = false;
    }

    private void AttachServerEvents()
    {
        if (serverEventsAttached)
        {
            return;
        }

        NetworkServer.OnDisconnectedEvent = (Action<NetworkConnectionToClient>)Delegate.Combine(
            NetworkServer.OnDisconnectedEvent,
            new Action<NetworkConnectionToClient>(HandleServerDisconnect));
        serverEventsAttached = true;
    }

    private void DetachServerEvents()
    {
        if (!serverEventsAttached)
        {
            return;
        }

        NetworkServer.OnDisconnectedEvent = (Action<NetworkConnectionToClient>)Delegate.Remove(
            NetworkServer.OnDisconnectedEvent,
            new Action<NetworkConnectionToClient>(HandleServerDisconnect));
        serverEventsAttached = false;
    }

    private void TrackConnectionDrop()
    {
        bool clientActive = NetworkClient.active;
        if (wasClientActive && !clientActive)
        {
            statusLine = "客户端已断开，已保留上次会话信息。";
            if (lastSession != null)
            {
                lastSession.LastSeenUtc = DateTime.UtcNow;
                store.SaveLastSession(lastSession);
            }
        }

        if (!wasClientActive && clientActive)
        {
            autoHelloAttempts = 0;
            clientHelloAccepted = false;
            nextHelloAt = 0f;
        }

        wasClientActive = clientActive;
    }

    private void TrackHostSession()
    {
        bool serverActive = NetworkServer.active;
        if (wasServerActive && !serverActive)
        {
            lastObservedFloorGuid = "";
            lastPublishedLobbyState = "";
            nextHostSessionRefreshAt = 0f;
            nextHostPlayerRefreshAt = 0f;
            nextHostLobbyRefreshAt = 0f;
            nextHostLobbyPublishAt = 0f;
        }

        wasServerActive = serverActive;
        if (!serverActive)
        {
            if (reconnectJoinWindowOpen)
            {
                CloseReconnectJoinWindow();
            }
            else
            {
                ResetReconnectJoinWindowState();
            }
            DetachServerEvents();
            return;
        }

        AttachServerEvents();
        MaintainReconnectJoinWindow();
        float now = Time.unscaledTime;
        if (hostSession == null || now >= nextHostSessionRefreshAt)
        {
            nextHostSessionRefreshAt = now + Mathf.Max(1, config.HostSessionRefreshSeconds);
            EnsureHostSession();
        }

        if (hostSession != null && now >= nextHostPlayerRefreshAt)
        {
            nextHostPlayerRefreshAt = now + Mathf.Max(1, config.HostPlayerRefreshSeconds);
            RefreshHostMembersFromPlayerSpawners();
            AlignPlayerSaveSlotsFromSession();
        }

        if (hostSession != null && now >= nextHostLobbyRefreshAt)
        {
            nextHostLobbyRefreshAt = now + Mathf.Max(3, config.HostLobbyRefreshSeconds);
            RefreshHostMembersFromLobby();
        }

        if (hostSession != null && now >= nextHostLobbyPublishAt)
        {
            nextHostLobbyPublishAt = now + Mathf.Max(3, config.HostLobbyPublishSeconds);
            PublishLobbySessionData();
        }

        if (Time.unscaledTime >= nextSaveHostSessionAt)
        {
            nextSaveHostSessionAt = Time.unscaledTime + 10f;
            SaveHostSession();
        }
    }

    private void OpenReconnectJoinWindow(string reason)
    {
        if (!NetworkServer.active || config == null || !config.ForceOpenInDungeonJoinForReconnect)
        {
            return;
        }

        if (!reconnectJoinWindowOpen)
        {
            reconnectPreviousAllowConnection = HorayNetworkAuthenticator.allowConnection;
            reconnectPreviousAccessDenyInDungeon = HorayNetworkAuthenticator.AccessDeny_InDungeon;
            reconnectJoinWindowOpen = true;
        }

        HorayNetworkAuthenticator.allowConnection = true;
        HorayNetworkAuthenticator.AccessDeny_InDungeon = false;
        float seconds = Mathf.Max(30, config.ReconnectJoinWindowSeconds);
        float until = Time.unscaledTime + seconds;
        if (until > reconnectConnectionOpenUntil)
        {
            reconnectConnectionOpenUntil = until;
            Debug.Log("[SephiriaReconnect] Opened reconnect join window for " + seconds + "s: " + reason);
        }
    }

    private void MaintainReconnectJoinWindow()
    {
        if (!reconnectJoinWindowOpen)
        {
            return;
        }

        if (config == null || !config.ForceOpenInDungeonJoinForReconnect || Time.unscaledTime > reconnectConnectionOpenUntil)
        {
            CloseReconnectJoinWindow();
            return;
        }

        HorayNetworkAuthenticator.allowConnection = true;
        HorayNetworkAuthenticator.AccessDeny_InDungeon = false;
    }

    private void CloseReconnectJoinWindow()
    {
        if (!reconnectJoinWindowOpen)
        {
            return;
        }

        HorayNetworkAuthenticator.allowConnection = reconnectPreviousAllowConnection;
        HorayNetworkAuthenticator.AccessDeny_InDungeon = ShouldDenyJoinBecauseInDungeon()
            ? true
            : reconnectPreviousAccessDenyInDungeon;
        reconnectJoinWindowOpen = false;
        reconnectConnectionOpenUntil = 0f;
        RestoreLobbyJoinableAfterReconnectWindow();
        Debug.Log("[SephiriaReconnect] Closed reconnect join window.");
    }

    private void ResetReconnectJoinWindowState()
    {
        reconnectJoinWindowOpen = false;
        reconnectConnectionOpenUntil = 0f;
    }

    private void RestoreLobbyJoinableAfterReconnectWindow()
    {
        if (config == null || !config.ForceLobbyJoinableForReconnect || !ShouldDenyJoinBecauseInDungeon())
        {
            return;
        }

        try
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (lobby.HasLobby && lobby.Manager != null)
            {
                lobby.Manager.SetJoinable(makeJoinable: false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to restore Steam lobby joinable state: " + ex.Message);
        }
    }

    private static bool ShouldDenyJoinBecauseInDungeon()
    {
        try
        {
            return DungeonManager.Instance != null &&
                DungeonManager.Instance.dungeonEnvironment.GetValueOrDefault("IsInDungeon", 0) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void TrackClientHello()
    {
        if (!config.AutoSendHello || !NetworkClient.active || NetworkServer.active || clientHelloAccepted)
        {
            return;
        }

        if (Time.unscaledTime >= nextHelloAt)
        {
            if (autoHelloAttempts >= Mathf.Max(1, config.MaxAutoHelloAttempts))
            {
                return;
            }

            nextHelloAt = Time.unscaledTime + Mathf.Max(5, config.ClientHelloIntervalSeconds);
            autoHelloAttempts++;
            SendHello(reconnectAttempt: ShouldAttemptReconnectHello());
        }
    }

    private bool ShouldAttemptReconnectHello()
    {
        if (lastSession == null)
        {
            return false;
        }

        LobbyInfo lobby = GetLobbyInfo();
        string lobbySessionId = ReadLobbySessionId(lobby);
        return string.IsNullOrEmpty(lobbySessionId) || lobbySessionId == lastSession.SessionId;
    }

    private void EnsureHostSession()
    {
        LobbyInfo lobby = GetLobbyInfo();
        string saveSlot = GetSelectedSaveSlot();
        string runId = GetRunId();

        bool changedSaveOrRun = hostSession != null
            && (hostSession.SaveSlotId != saveSlot || hostSession.RunId != runId);
        bool changedHost = hostSession != null
            && hostSession.HostSteamId != 0
            && lobby.LocalSteamId != 0
            && hostSession.HostSteamId != lobby.LocalSteamId;
        bool needsNewSession = hostSession == null
            || changedSaveOrRun
            || changedHost;

        if (needsNewSession)
        {
            if (changedSaveOrRun)
            {
                DeleteEndedSessionCheckpoints();
            }

            hostSession = new ReconnectSessionRecord
            {
                HostSteamId = lobby.LocalSteamId,
                LobbyId = lobby.LobbyId,
                SessionId = Guid.NewGuid().ToString("N"),
                SaveSlotId = saveSlot,
                RunId = runId,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            statusLine = "已创建房主重连会话。";
            SaveHostSession();
        }
        else
        {
            if (hostSession.HostSteamId == 0 && lobby.LocalSteamId != 0)
            {
                hostSession.HostSteamId = lobby.LocalSteamId;
            }

            hostSession.LobbyId = lobby.LobbyId;
            hostSession.UpdatedUtc = DateTime.UtcNow;
            hostSession.Members ??= new Dictionary<ulong, ReconnectMemberRecord>();
            hostSession.Checkpoints ??= new List<ReconnectCheckpointRecord>();
        }
    }

    private void DeleteEndedSessionCheckpoints()
    {
        if (hostSession == null)
        {
            return;
        }

        if (hostSession.Checkpoints != null)
        {
            foreach (ReconnectCheckpointRecord checkpoint in hostSession.Checkpoints.Where(c => c != null).ToList())
            {
                store.DeleteCheckpointFiles(checkpoint);
            }
        }

        foreach (string checkpointId in store.EnumerateCheckpointIds().Distinct().ToList())
        {
            store.DeleteCheckpointFiles(checkpointId);
        }
    }

    private void RefreshHostMembersFromPlayerSpawners()
    {
        if (hostSession == null || PlayerSpawner.MultiplayerList == null)
        {
            return;
        }

        foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
        {
            if (spawner == null || spawner.steamID == 0)
            {
                continue;
            }

            ReconnectMemberRecord member = GetOrCreateMember(spawner.steamID);
            member.PlayerName = SafePlayerName(spawner);
            if (member.PlayerSlot < 0)
            {
                member.PlayerSlot = spawner.currentPlayerIdxForSave >= 0 ? spawner.currentPlayerIdxForSave : spawner.currentPlayerIdx;
            }
            member.SaveSlotId = hostSession.SaveSlotId;
            member.RunId = hostSession.RunId;
            member.SessionId = hostSession.SessionId;
            member.LastSeenUtc = DateTime.UtcNow;
            member.CurrentFloorId = SafeFloorGuid(spawner);
            member.CheckpointId = hostSession.CurrentCheckpointId;
            if (member.State == ReconnectMemberState.OfflineReserved || member.State == ReconnectMemberState.ReconnectPending)
            {
                member.State = ReconnectMemberState.ReconnectedWaiting;
            }
            else if (member.State != ReconnectMemberState.Removed && member.State != ReconnectMemberState.ExitedGracefully)
            {
                member.State = ReconnectMemberState.Online;
            }
        }
    }

    private void RefreshHostMembersFromLobby()
    {
        if (hostSession == null)
        {
            return;
        }

        LobbyInfo lobby = GetLobbyInfo();
        if (!lobby.HasLobby || lobby.Manager == null)
        {
            return;
        }

        try
        {
            LobbyMemberData[] members = lobby.Manager.Lobby.Members;
            for (int i = 0; i < members.Length; i++)
            {
                UserData user = members[i].user;
                ulong steamId = user.SteamId;
                if (steamId == 0)
                {
                    continue;
                }

                ReconnectMemberRecord member = GetOrCreateMember(steamId);
                if (string.IsNullOrEmpty(member.PlayerName))
                {
                    member.PlayerName = user.Nickname;
                }

                member.SaveSlotId = hostSession.SaveSlotId;
                member.RunId = hostSession.RunId;
                member.SessionId = hostSession.SessionId;
                member.LastSeenUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to refresh lobby members: " + ex.Message);
        }
    }

    private void AlignPlayerSaveSlotsFromSession()
    {
        if (hostSession == null || PlayerSpawner.MultiplayerList == null)
        {
            return;
        }

        foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
        {
            if (spawner == null || spawner.steamID == 0)
            {
                continue;
            }

            if (!hostSession.Members.TryGetValue(spawner.steamID, out ReconnectMemberRecord member))
            {
                continue;
            }

            if (member.PlayerSlot < 0)
            {
                continue;
            }

            if (spawner.currentPlayerIdxForSave != member.PlayerSlot)
            {
                Debug.Log("[SephiriaReconnect] Correcting player save slot for SteamID " + spawner.steamID + " from " + spawner.currentPlayerIdxForSave + " to " + member.PlayerSlot);
                spawner.currentPlayerIdxForSave = member.PlayerSlot;
            }
        }
    }

    private void PublishLobbySessionData()
    {
        if (hostSession == null)
        {
            return;
        }

        string state = hostSession.SessionId + "|" + hostSession.CurrentCheckpointId + "|" + hostSession.CurrentCheckpointHash + "|" + (hostSession.Members == null ? 0 : hostSession.Members.Count);
        if (state == lastPublishedLobbyState)
        {
            return;
        }

        LobbyInfo lobby = GetLobbyInfo();
        if (!lobby.HasLobby || !lobby.IsOwner || lobby.Manager == null)
        {
            return;
        }

        try
        {
            LobbyData lobbyData = lobby.Manager.Lobby;
            lobbyData["ReconnectMod"] = ProtocolVersion;
            lobbyData["ReconnectSessionId"] = hostSession.SessionId;
            lobbyData["ReconnectCheckpointId"] = hostSession.CurrentCheckpointId ?? "";
            lobbyData["ReconnectCheckpointHash"] = hostSession.CurrentCheckpointHash ?? "";
            lastPublishedLobbyState = state;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to publish lobby data: " + ex.Message);
        }
    }

    public static void NotifyFloorEntryCheckpointCandidate(bool allowSave, string floorGuid, bool runStarted)
    {
        Instance?.HandleFloorEntryCheckpointCandidate(allowSave, floorGuid, runStarted);
    }

    private void HandleFloorEntryCheckpointCandidate(bool allowSave, string floorGuid, bool runStarted)
    {
        if (!config.AutoCaptureFloorCheckpoint || !allowSave || !runStarted)
        {
            return;
        }

        if (!NetworkServer.active)
        {
            return;
        }

        if (hostSession == null)
        {
            EnsureHostSession();
        }

        if (hostSession == null)
        {
            return;
        }

        PlayerSpawner local = GetLocalSpawner();
        if (local == null || local.PlayerAvatar == null || local.PlayerAvatar.loadingScreenType != -1)
        {
            return;
        }

        if (string.IsNullOrEmpty(floorGuid) || floorGuid == lastObservedFloorGuid)
        {
            return;
        }

        if (!string.Equals(local.PlayerAvatar.currentFloorGuid, floorGuid, StringComparison.Ordinal))
        {
            return;
        }

        if (local.PlayerAvatar.isInDungeon <= 0)
        {
            return;
        }

        if (!ShouldCaptureFloorEntryCheckpoint(local.PlayerAvatar, floorGuid))
        {
            return;
        }

        lastObservedFloorGuid = floorGuid;
        CaptureCheckpoint("floor-entry", activateForRestore: true, floorGuid);
    }

    private static bool ShouldCaptureFloorEntryCheckpoint(PlayerAvatar avatar, string floorGuid)
    {
        if (avatar == null || string.IsNullOrEmpty(floorGuid))
        {
            return false;
        }

        if (SaveManager.CurrentRun == null || !SaveManager.CurrentRun.GetBool("RunStarted", fallback: false))
        {
            return false;
        }

        DungeonManager dungeon = DungeonManager.Instance;
        if (dungeon == null || !dungeon.isRunStarted)
        {
            return false;
        }

        if (!dungeon.generatedFloors.TryGetValue(floorGuid, out FloorData floor) || floor == null)
        {
            return false;
        }

        if (string.Equals(floor.name, "MultiZone", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            StageEntity lobbyStage = dungeon.Race != null ? dungeon.Race.lobbyStage : null;
            if (lobbyStage != null && string.Equals(floor.stageName, lobbyStage.name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
        }

        return true;
    }

    private void CaptureCheckpoint(string reason, bool activateForRestore, string floorGuidOverride = null)
    {
        if (!NetworkServer.active || hostSession == null)
        {
            statusLine = "已跳过：只有房主/服务端可以保存检查点。";
            return;
        }

        if (SaveManager.CurrentRun == null)
        {
            statusLine = "已跳过：当前没有可用的局内存档。";
            return;
        }

        try
        {
            string floorGuid = string.IsNullOrEmpty(floorGuidOverride) ? GetLocalFloorGuid() : floorGuidOverride;
            FlushCurrentRunForCheckpoint(floorGuid);

            if (PlayerSpawner.MultiplayerList != null)
            {
                foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
                {
                    spawner?.SaveCurrentSessionData();
                }
            }

            FloorData floor = FindFloor(floorGuid);
            string checkpointId = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + ShortGuid();
            string snapshotPath = store.GetCheckpointSavePath(checkpointId);
            SaveData snapshot = SaveManager.CurrentRun.Copy();
            snapshot.enableCloudSave = false;
            snapshot.Save(snapshotPath);

            string hash = ComputeSaveHash(SaveManager.CurrentRun);
            ReconnectCheckpointRecord checkpoint = new ReconnectCheckpointRecord
            {
                CheckpointId = checkpointId,
                CheckpointHash = hash,
                Reason = reason,
                IsRestoreTarget = activateForRestore,
                FloorGuid = floorGuid,
                FloorName = floor == null ? "" : floor.name,
                StageName = floor == null ? "" : floor.stageName,
                RunId = hostSession.RunId,
                SaveSlotId = hostSession.SaveSlotId,
                SnapshotPath = snapshotPath,
                CreatedUtc = DateTime.UtcNow
            };

            hostSession.CurrentFloorId = floorGuid;
            if (activateForRestore)
            {
                hostSession.CurrentCheckpointId = checkpoint.CheckpointId;
                hostSession.CurrentCheckpointHash = checkpoint.CheckpointHash;
            }
            hostSession.Checkpoints.Add(checkpoint);

            store.SaveCheckpointMeta(checkpoint);
            PruneCheckpoints();
            SaveHostSession();
            statusLine = activateForRestore ? "已保存楼层入口检查点：" + checkpoint.CheckpointId : "已保存非恢复检查点：" + checkpoint.CheckpointId;
            ShowSystemMessage(activateForRestore ? "已保存楼层入口重连点。" : "已保存非恢复检查点。");
        }
        catch (Exception ex)
        {
            statusLine = "保存检查点失败：" + ex.Message;
            Debug.LogError("[SephiriaReconnect] Checkpoint failed: " + ex);
        }
    }

    private static void FlushCurrentRunForCheckpoint(string floorGuid)
    {
        if (SaveManager.CurrentRun == null || string.IsNullOrEmpty(floorGuid))
        {
            return;
        }

        DungeonManager dungeon = DungeonManager.Instance;
        if (dungeon != null)
        {
            try
            {
                DungeonSaveCurrentSessionDataMethod?.Invoke(dungeon, new object[] { floorGuid });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SephiriaReconnect] Failed to flush DungeonManager session data before checkpoint: " + ex.Message);
            }
        }

        SaveManager.CurrentRun.SetBool("RunStarted", value: true);
        SaveManager.CurrentRun.SetString("LastFloorGuid", floorGuid);
        UpsertDungeonEnvironmentValue("IsInDungeon", 1);
    }

    private static void UpsertDungeonEnvironmentValue(string key, int value)
    {
        if (SaveManager.CurrentRun == null || string.IsNullOrEmpty(key))
        {
            return;
        }

        int count = SaveManager.CurrentRun.GetInt("DungeonEnvironmentCount", 0);
        for (int i = 0; i < count; i++)
        {
            string currentKey = SaveManager.CurrentRun.GetString($"DungeonEnvironment{i}_Key", "");
            if (string.Equals(currentKey, key, StringComparison.Ordinal))
            {
                SaveManager.CurrentRun.SetInt($"DungeonEnvironment{i}_Value", value);
                return;
            }
        }

        SaveManager.CurrentRun.SetString($"DungeonEnvironment{count}_Key", key);
        SaveManager.CurrentRun.SetInt($"DungeonEnvironment{count}_Value", value);
        SaveManager.CurrentRun.SetInt("DungeonEnvironmentCount", count + 1);
    }

    private void PruneCheckpoints()
    {
        if (hostSession == null || hostSession.Checkpoints == null)
        {
            return;
        }

        int maxCount = Mathf.Max(1, config.MaxCheckpointCount);
        int maxOrphanDelete = Mathf.Max(1, config.MaxCheckpointPruneBatch);
        string activeCheckpointId = hostSession.CurrentCheckpointId ?? "";
        int orphanDeleted = 0;

        hostSession.Checkpoints = hostSession.Checkpoints
            .Where(c => c != null && !string.IsNullOrEmpty(c.CheckpointId))
            .GroupBy(c => c.CheckpointId)
            .Select(g => g.OrderByDescending(c => c.CreatedUtc).First())
            .OrderBy(c => c.CreatedUtc)
            .ToList();

        while (hostSession.Checkpoints.Count > maxCount)
        {
            int removeIndex = hostSession.Checkpoints.FindIndex(c => c.CheckpointId != activeCheckpointId);
            if (removeIndex < 0)
            {
                break;
            }

            ReconnectCheckpointRecord removed = hostSession.Checkpoints[removeIndex];
            hostSession.Checkpoints.RemoveAt(removeIndex);
            store.DeleteCheckpointFiles(removed);
        }

        HashSet<string> keptIds = new HashSet<string>(hostSession.Checkpoints.Select(c => c.CheckpointId));
        foreach (string checkpointId in store.EnumerateCheckpointIds().Distinct().OrderBy(id => id).ToList())
        {
            if (orphanDeleted >= maxOrphanDelete)
            {
                break;
            }

            if (keptIds.Contains(checkpointId) || checkpointId == activeCheckpointId)
            {
                continue;
            }

            store.DeleteCheckpointFiles(checkpointId);
            orphanDeleted++;
        }
    }

    private void PrepareRestoreCurrentCheckpoint()
    {
        if (!NetworkServer.active)
        {
            statusLine = "已跳过：只有房主/服务端可以准备恢复。";
            return;
        }

        if (hostSession == null || string.IsNullOrEmpty(hostSession.CurrentCheckpointId))
        {
            statusLine = "当前没有可恢复的检查点。";
            return;
        }

        if (HasUnreturnedReservedMember())
        {
            statusLine = config.RequireAllPlayersBeforeRestore
                ? "还有掉线成员未回房；按配置仍继续执行本层恢复。"
                : "还有掉线成员未回房；将先恢复本层，之后可邀请成员回房。";
            ShowSystemMessage(statusLine);
        }

        ReconnectCheckpointRecord checkpoint = hostSession.Checkpoints.LastOrDefault(c => c.CheckpointId == hostSession.CurrentCheckpointId);
        if (checkpoint == null || !File.Exists(checkpoint.SnapshotPath))
        {
            statusLine = "检查点快照文件不存在。";
            return;
        }

        if (!ValidateCheckpointSnapshot(checkpoint, out string invalidReason))
        {
            if (!TryRefreshCheckpointSnapshotFromCurrentRun(checkpoint) || !ValidateCheckpointSnapshot(checkpoint, out invalidReason))
            {
                statusLine = "检查点不可恢复：" + invalidReason;
                ShowSystemMessage(statusLine);
                return;
            }
        }

        try
        {
            string selectedProfile = GetSelectedSaveSlot();
            string target = Path.Combine(SaveData.CommonPath, selectedProfile + "TMP.sav");
            string backup = target + ".reconnect-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            if (File.Exists(target))
            {
                File.Copy(target, backup, overwrite: false);
            }

            File.Copy(checkpoint.SnapshotPath, target, overwrite: true);
            StartCoroutine(RestoreHostFromCheckpointCoroutine(selectedProfile, checkpoint.CheckpointId));
            statusLine = "正在执行本层恢复。";
            ShowSystemMessage("正在执行本层恢复。");
        }
        catch (Exception ex)
        {
            statusLine = "准备恢复失败：" + ex.Message;
            Debug.LogError("[SephiriaReconnect] Prepare restore failed: " + ex);
        }
    }

    private IEnumerator RestoreHostFromCheckpointCoroutine(string selectedProfile, string checkpointId)
    {
        HorayNetworkManager manager = NetworkManager.singleton as HorayNetworkManager;
        if (manager == null)
        {
            statusLine = "恢复失败：未找到网络管理器。";
            yield break;
        }

        manager.requestSelfLeave = true;
        HorayNetworkAuthenticator.allowConnection = true;
        statusLine = "正在停止主机并载入检查点……";
        pendingLobbySettings = CaptureCurrentLobbySettings();
        PrepareRuntimeStateForRestore();
        ResetPlayerEffectHud();
        NetworkManager.singleton.StopHost();
        yield return null;
        yield return new WaitForSeconds(1f);
        ResetPlayerEffectHud();

        bool loadedProfile = SaveManager.Load(selectedProfile);
        bool loadedTmp = SaveManager.LoadTMP(selectedProfile);
        SaveManager.ApplyPostLoadSaveFixes();
        if (!loadedProfile || !loadedTmp)
        {
            statusLine = "恢复失败：无法重新载入检查点存档。";
            ShowSystemMessage(statusLine);
            yield break;
        }

        statusLine = "正在重启主机并回到检查点楼层……";
        NetworkManager.singleton.StartHost();
        yield return new WaitForSeconds(1f);
        OpenReconnectJoinWindow("restore-host-started");
        EnsureHostSession();
        yield return EnsureSteamLobbyAndPublishAfterRestore();
        statusLine = "已请求恢复到检查点：" + checkpointId;
        ShowSystemMessage("已请求恢复到检查点楼层。");
    }

    private bool TryRefreshCheckpointSnapshotFromCurrentRun(ReconnectCheckpointRecord checkpoint)
    {
        if (checkpoint == null || SaveManager.CurrentRun == null || !NetworkServer.active)
        {
            return false;
        }

        string localFloorGuid = GetLocalFloorGuid();
        if (string.IsNullOrEmpty(localFloorGuid) || !string.Equals(localFloorGuid, checkpoint.FloorGuid, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            FlushCurrentRunForCheckpoint(checkpoint.FloorGuid);
            if (PlayerSpawner.MultiplayerList != null)
            {
                foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
                {
                    spawner?.SaveCurrentSessionData();
                }
            }

            SaveData snapshot = SaveManager.CurrentRun.Copy();
            snapshot.enableCloudSave = false;
            snapshot.Save(checkpoint.SnapshotPath);
            checkpoint.CheckpointHash = ComputeSaveHash(SaveManager.CurrentRun);
            if (hostSession != null && hostSession.CurrentCheckpointId == checkpoint.CheckpointId)
            {
                hostSession.CurrentCheckpointHash = checkpoint.CheckpointHash;
            }

            store.SaveCheckpointMeta(checkpoint);
            SaveHostSession();
            Debug.LogWarning("[SephiriaReconnect] Repaired checkpoint snapshot before restore: " + checkpoint.CheckpointId);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to repair checkpoint snapshot before restore: " + ex.Message);
            return false;
        }
    }

    private static bool ValidateCheckpointSnapshot(ReconnectCheckpointRecord checkpoint, out string reason)
    {
        reason = "";
        if (checkpoint == null || string.IsNullOrEmpty(checkpoint.SnapshotPath) || !File.Exists(checkpoint.SnapshotPath))
        {
            reason = "快照文件不存在。";
            return false;
        }

        try
        {
            SaveData data = new SaveData(useEncryption: true);
            if (!data.LoadFromString(File.ReadAllText(checkpoint.SnapshotPath)))
            {
                reason = "快照无法读取。";
                return false;
            }

            if (!data.GetBool("RunStarted", fallback: false))
            {
                reason = "快照不是局内状态。";
                return false;
            }

            string savedFloorGuid = data.GetString("LastFloorGuid", "");
            if (string.IsNullOrEmpty(savedFloorGuid))
            {
                reason = "快照缺少楼层记录。";
                return false;
            }

            if (!string.IsNullOrEmpty(checkpoint.FloorGuid) && !string.Equals(savedFloorGuid, checkpoint.FloorGuid, StringComparison.Ordinal))
            {
                reason = "快照楼层和检查点记录不一致。";
                return false;
            }

            if (!SnapshotHasDungeonEnvironmentValue(data, "IsInDungeon", 1))
            {
                reason = "快照缺少地牢状态。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool SnapshotHasDungeonEnvironmentValue(SaveData data, string key, int value)
    {
        int count = data.GetInt("DungeonEnvironmentCount", 0);
        for (int i = 0; i < count; i++)
        {
            string currentKey = data.GetString($"DungeonEnvironment{i}_Key", "");
            int currentValue = data.GetInt($"DungeonEnvironment{i}_Value", 0);
            if (string.Equals(currentKey, key, StringComparison.Ordinal) && currentValue == value)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator EnsureSteamLobbyAndPublishAfterRestore()
    {
        if (!NetworkServer.active)
        {
            yield break;
        }

        try
        {
            OpenReconnectJoinWindow("ensure-lobby-after-restore");
            LobbyInfo lobby = GetLobbyInfo();
            if (lobby.HasLobby && lobby.LobbyId != 0)
            {
                ApplyLobbySettingsSnapshot(lobby);
                if (hostSession != null)
                {
                    hostSession.LobbyId = lobby.LobbyId;
                }
                ForcePublishLobbySessionData();
                yield break;
            }

            if (lobby.Manager != null)
            {
                lobby.Manager.Create();
                OpenReconnectJoinWindow("create-lobby-after-restore");
                statusLine = "已恢复本层，正在重新创建 Steam 房间。";
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to ensure Steam lobby after restore: " + ex.Message);
            yield break;
        }

        float deadline = Time.unscaledTime + 8f;
        while (Time.unscaledTime < deadline)
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (lobby.HasLobby && lobby.LobbyId != 0)
            {
                ApplyLobbySettingsSnapshot(lobby);
                OpenReconnectJoinWindow("lobby-ready-after-restore");
                if (hostSession != null)
                {
                    hostSession.LobbyId = lobby.LobbyId;
                    SaveHostSession();
                }

                ForcePublishLobbySessionData();
                yield break;
            }

            yield return null;
        }

        statusLine = "已恢复本层，但 Steam 房间尚未就绪。";
        ShowSystemMessage(statusLine);
    }

    private LobbySettingsSnapshot CaptureCurrentLobbySettings()
    {
        try
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (!lobby.HasLobby || lobby.Manager == null)
            {
                return null;
            }

            LobbyData data = lobby.Manager.Lobby;
            return new LobbySettingsSnapshot
            {
                Name = data.Name,
                GameVersion = data.GameVersion,
                Type = data.Type,
                MaxMembers = data.MaxMembers,
                Chapter = data["Chapter"]
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to capture Steam lobby settings before restore: " + ex.Message);
            return null;
        }
    }

    private void ApplyLobbySettingsSnapshot(LobbyInfo lobby)
    {
        if (!lobby.HasLobby || lobby.Manager == null)
        {
            return;
        }

        try
        {
            LobbyData data = lobby.Manager.Lobby;
            if (config == null || config.ForceLobbyJoinableForReconnect)
            {
                lobby.Manager.SetJoinable(makeJoinable: true);
            }

            if (pendingLobbySettings == null)
            {
                data.Type = ELobbyType.k_ELobbyTypePrivate;
                data.MaxMembers = 4;
                data.GameVersion = Application.version;
                data.Name = BuildFallbackLobbyName();
                data["Chapter"] = BuildLobbyChapterValue();
                data.SetGameServer(UserData.Me.id);
                return;
            }

            if (!string.IsNullOrEmpty(pendingLobbySettings.Name))
            {
                data.Name = pendingLobbySettings.Name;
            }

            data.GameVersion = string.IsNullOrEmpty(pendingLobbySettings.GameVersion)
                ? Application.version
                : pendingLobbySettings.GameVersion;

            data.Type = pendingLobbySettings.Type;
            if (pendingLobbySettings.MaxMembers > 0)
            {
                data.MaxMembers = pendingLobbySettings.MaxMembers;
            }

            data["Chapter"] = string.IsNullOrEmpty(pendingLobbySettings.Chapter) || !IsValidLobbyChapterValue(pendingLobbySettings.Chapter)
                ? BuildLobbyChapterValue()
                : pendingLobbySettings.Chapter;

            data.SetGameServer(UserData.Me.id);
            pendingLobbySettings = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to restore Steam lobby settings after restore: " + ex.Message);
        }
    }

    private static string BuildFallbackLobbyName()
    {
        string playerName = SafeLocalPlayerName();
        return string.IsNullOrEmpty(playerName) ? "Sephiria Reconnect" : playerName + "的房间";
    }

    private static string BuildLobbyChapterValue()
    {
        try
        {
            DungeonManager dungeon = DungeonManager.Instance;
            PlayerSpawner spawner = GetLocalSpawner();
            PlayerAvatar avatar = spawner != null ? spawner.PlayerAvatar : null;
            PlayerLocalDataStorage localData = avatar != null ? avatar.GetComponent<PlayerLocalDataStorage>() : null;

            if (localData != null)
            {
                if (localData.mainQuestProgress < 11 && dungeon != null)
                {
                    RaceEntity race = RaceDatabase.FindById(dungeon.raceId);
                    if (race != null)
                    {
                        return race.actualChapterNum.ToString();
                    }
                }

                int chapterNum;
                int subChapterNum;
                if (QuestDatabase.TryGetCurrentMainQuestChapter(localData.GetCurrentMainQuestNode(), out chapterNum, out subChapterNum))
                {
                    return subChapterNum > 0 ? chapterNum + "-" + subChapterNum : chapterNum.ToString();
                }
            }

            if (dungeon != null)
            {
                RaceEntity race = RaceDatabase.FindById(dungeon.raceId);
                if (race != null)
                {
                    return race.actualChapterNum.ToString();
                }
            }

            if (SaveManager.CurrentRun != null)
            {
                RaceEntity race = RaceDatabase.FindById(SaveManager.CurrentRun.GetInt("CurrentGame", -1));
                if (race != null)
                {
                    return race.actualChapterNum.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to build Steam lobby chapter value: " + ex.Message);
        }

        return "0";
    }

    private static bool IsValidLobbyChapterValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] parts = value.Split('-');
        if (parts.Length == 1)
        {
            return int.TryParse(parts[0], out _);
        }

        return parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _);
    }

    private void ForcePublishLobbySessionData()
    {
        lastPublishedLobbyState = "";
        PublishLobbySessionData();
    }

    private static void PrepareRuntimeStateForRestore()
    {
        if (!NetworkServer.active)
        {
            return;
        }

        HashSet<int> cleanedObjects = new HashSet<int>();
        try
        {
            foreach (KeyValuePair<int, NetworkConnectionToClient> connection in NetworkServer.connections)
            {
                TryResetPlayerRuntime(connection.Value != null ? connection.Value.identity : null, cleanedObjects);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to scan network connections before restore: " + ex.Message);
        }

        try
        {
            if (PlayerSpawner.MultiplayerList != null)
            {
                foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
                {
                    TryResetPlayerRuntime(spawner, cleanedObjects);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to scan multiplayer list before restore: " + ex.Message);
        }

        try
        {
            if (DungeonManager.Instance != null && DungeonManager.Instance.globalItemStatTable != null)
            {
                DungeonManager.Instance.globalItemStatTable.Clear();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to clear global item stat table before restore: " + ex.Message);
        }
    }

    private static void TryResetPlayerRuntime(NetworkIdentity identity, HashSet<int> cleanedObjects)
    {
        if (identity == null)
        {
            return;
        }

        if (!cleanedObjects.Add(identity.gameObject.GetInstanceID()))
        {
            return;
        }

        try
        {
            MiracleController miracle = identity.GetComponent<MiracleController>();
            if (miracle != null)
            {
                miracle.ClearMiracle();
            }

            PlayerAvatar avatar = identity.GetComponent<PlayerAvatar>();
            PlayerSpawner spawner = identity.GetComponent<PlayerSpawner>();
            TryResetPlayerRuntime(avatar, spawner);
        }
        catch (Exception ex)
        {
            Debug.LogError("[SephiriaReconnect] Error resetting player runtime before restore: " + ex);
        }
    }

    private static void TryResetPlayerRuntime(PlayerSpawner spawner, HashSet<int> cleanedObjects)
    {
        if (spawner == null)
        {
            return;
        }

        NetworkIdentity identity = spawner.GetComponent<NetworkIdentity>();
        if (identity != null)
        {
            TryResetPlayerRuntime(identity, cleanedObjects);
            return;
        }

        if (!cleanedObjects.Add(spawner.gameObject.GetInstanceID()))
        {
            return;
        }

        TryResetPlayerRuntime(spawner.PlayerAvatar, spawner);
    }

    private static void TryResetPlayerRuntime(PlayerAvatar avatar, PlayerSpawner fallbackSpawner)
    {
        if (avatar == null)
        {
            return;
        }

        if (avatar.Inventory != null)
        {
            avatar.Inventory.ForceRemoveAll();
            avatar.Inventory.ClearDungeonTempLevels();
            avatar.Inventory.ClearItemDropBonus();
            avatar.Inventory.NetworkadaptiveItemDropBonus = 0;
        }

        PlayerSpawner spawner = avatar.spawner != null ? avatar.spawner : fallbackSpawner;
        if (spawner != null)
        {
            spawner.consumeFruitSkewerBonus.Clear();
            spawner.NetworkadditionalAdaptiveItemDropBonus = 0;
        }

        avatar.savableParameters.Clear();
        avatar.ClearBuffs();
        avatar.SetMoney(0);
        avatar.NetworkreservedMp = 0;
        avatar.NetworkreceivedDamage = 0f;
        avatar.NetworkreceivedShieldDamage = 0f;
        avatar.ClearOrphanedStatusInstance();
    }

    private static void ResetPlayerEffectHud()
    {
        try
        {
            if (UIManager.Instance == null)
            {
                return;
            }

            UI_PlayerEffectHUD effectHud = UIManager.Instance.GetElement<UI_PlayerEffectHUD>();
            if (effectHud == null)
            {
                return;
            }

            effectHud.Disconnect();
            ClearEffectHudDictionary(effectHud, "instantiatedHUDs");
            ClearEffectHudDictionary(effectHud, "instantiatedBoons");
            ClearEffectHudDictionary(effectHud, "instantiatedConditions");
            ClearEffectHudZone(effectHud.boonZone);
            ClearEffectHudZone(effectHud.conditionZone);
            effectHud.RefreshBoonAndConditionZone();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to reset player effect HUD: " + ex.Message);
        }
    }

    private static void ClearEffectHudDictionary(UI_PlayerEffectHUD effectHud, string fieldName)
    {
        FieldInfo field = typeof(UI_PlayerEffectHUD).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || !(field.GetValue(effectHud) is IDictionary dictionary))
        {
            return;
        }

        foreach (object value in dictionary.Values)
        {
            if (value is UI_EffectHUD_Basic hud && hud != null)
            {
                Destroy(hud.gameObject);
            }
        }

        dictionary.Clear();
    }

    private static void ClearEffectHudZone(RectTransform zone)
    {
        if (zone == null)
        {
            return;
        }

        for (int i = zone.childCount - 1; i >= 0; i--)
        {
            Transform child = zone.GetChild(i);
            if (child != null)
            {
                Destroy(child.gameObject);
            }
        }
    }

    private void JoinLastLobby()
    {
        if (lastSession == null || lastSession.LobbyId == 0)
        {
            statusLine = "没有记录到上次房间。";
            return;
        }

        try
        {
            GameObject steam = SingletonObject.Find("SteamManager");
            if (!steam || !steam.TryGetComponent<LobbyManager>(out var lobbyManager))
            {
                statusLine = "未找到 Steam 房间管理器。";
                return;
            }

            LobbyData lobby = LobbyData.Get(lastSession.LobbyId.ToString());
            StartCoroutine(JoinLastLobbyCoroutine(lobbyManager, lobby));
            statusLine = "正在返回上次房间：" + lastSession.LobbyId;
        }
        catch (Exception ex)
        {
            statusLine = "加入上次房间失败：" + ex.Message;
            Debug.LogError("[SephiriaReconnect] Join last lobby failed: " + ex);
        }
    }

    private IEnumerator JoinLastLobbyCoroutine(LobbyManager lobbyManager, LobbyData lobby)
    {
        lobby.RequestData();
        lobbyManager.Join(lobby);
        float deadline = Time.unscaledTime + 8f;
        while (Time.unscaledTime < deadline)
        {
            if (lobbyManager.HasLobby && lobbyManager.Lobby.SteamId.m_SteamID == lobby.SteamId.m_SteamID)
            {
                SteamInvitation.ConnectToOtherHost(lobby.Owner.user.id.ToString());
                statusLine = "已回到上次房间，正在连接房主。";
                yield break;
            }

            yield return null;
        }

        statusLine = "加入上次房间超时。";
        ShowSystemMessage(statusLine);
    }

    private void SendHello(bool reconnectAttempt)
    {
        if (!NetworkClient.active)
        {
            statusLine = "当前客户端未连接，无法发送握手。";
            return;
        }

        LobbyInfo lobby = GetLobbyInfo();
        string lobbySessionId = ReadLobbySessionId(lobby);
        ReconnectHelloMessage msg = new ReconnectHelloMessage
        {
            protocolVersion = ProtocolVersion,
            playerSteamId = lobby.LocalSteamId,
            playerName = SafeLocalPlayerName(),
            saveSlotId = GetSelectedSaveSlot(),
            runId = GetRunId(),
            sessionId = reconnectAttempt ? lastSession?.SessionId ?? lobbySessionId : lobbySessionId,
            reconnectToken = reconnectAttempt ? lastSession?.ReconnectToken ?? "" : "",
            checkpointId = reconnectAttempt ? lastSession?.CheckpointId ?? "" : "",
            checkpointHash = reconnectAttempt ? lastSession?.CheckpointHash ?? "" : "",
            reconnectAttempt = reconnectAttempt
        };

        try
        {
            NetworkClient.Send(msg);
            statusLine = "已发送重连握手。";
        }
        catch (Exception ex)
        {
            statusLine = "发送握手失败：" + ex.Message;
            Debug.LogError("[SephiriaReconnect] Send hello failed: " + ex);
        }
    }

    private void HandleServerHello(NetworkConnectionToClient conn, ReconnectHelloMessage msg)
    {
        EnsureHostSession();
        ReconnectHelloReplyMessage reply = ValidateHello(msg);
        if (reply.accepted)
        {
            ReconnectMemberRecord member = GetOrCreateMember(msg.playerSteamId);
            member.PlayerName = string.IsNullOrWhiteSpace(msg.playerName) ? member.PlayerName : msg.playerName;
            member.ModInstalled = true;
            member.LastHelloUtc = DateTime.UtcNow;
            if (member.PlayerSlot < 0 && conn != null && conn.identity != null && conn.identity.TryGetComponent<PlayerSpawner>(out PlayerSpawner helloSpawner))
            {
                member.PlayerSlot = helloSpawner.currentPlayerIdxForSave >= 0 ? helloSpawner.currentPlayerIdxForSave : helloSpawner.currentPlayerIdx;
            }
            member.SaveSlotId = msg.saveSlotId;
            member.RunId = msg.runId;
            member.SessionId = hostSession.SessionId;
            member.LastSeenUtc = DateTime.UtcNow;
            member.CheckpointId = hostSession.CurrentCheckpointId;
            member.State = msg.reconnectAttempt ? ReconnectMemberState.ReconnectedWaiting : ReconnectMemberState.Online;
            if (conn != null && conn.identity != null && conn.identity.TryGetComponent<PlayerSpawner>(out PlayerSpawner reconnectSpawner) && member.PlayerSlot >= 0)
            {
                reconnectSpawner.currentPlayerIdxForSave = member.PlayerSlot;
            }
            SaveHostSession();
        }

        try
        {
            conn?.Send(reply);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to reply hello: " + ex.Message);
        }
    }

    private ReconnectHelloReplyMessage ValidateHello(ReconnectHelloMessage msg)
    {
        ReconnectHelloReplyMessage reply = new ReconnectHelloReplyMessage
        {
            accepted = false,
            reason = "",
            hostSteamId = hostSession.HostSteamId,
            lobbyId = hostSession.LobbyId,
            saveSlotId = hostSession.SaveSlotId,
            runId = hostSession.RunId,
            sessionId = hostSession.SessionId,
            checkpointId = hostSession.CurrentCheckpointId ?? "",
            checkpointHash = hostSession.CurrentCheckpointHash ?? ""
        };

        if (msg.protocolVersion != ProtocolVersion)
        {
            reply.reason = "模组协议版本不匹配。";
            return reply;
        }

        if (msg.playerSteamId == 0)
        {
            reply.reason = "无法识别玩家 SteamID。";
            return reply;
        }

        if (!string.IsNullOrEmpty(msg.saveSlotId) && msg.saveSlotId != hostSession.SaveSlotId)
        {
            reply.reason = "存档槽不匹配。";
            return reply;
        }

        if (!string.IsNullOrEmpty(msg.runId) && msg.runId != hostSession.RunId)
        {
            reply.reason = "局内进度不匹配。";
            return reply;
        }

        if (!string.IsNullOrEmpty(msg.sessionId) && msg.sessionId != hostSession.SessionId)
        {
            reply.reason = "会话编号不匹配。";
            return reply;
        }

        bool knownMember = hostSession.Members.TryGetValue(msg.playerSteamId, out ReconnectMemberRecord member);
        if (msg.reconnectAttempt)
        {
            if (!knownMember)
            {
                reply.reason = "房主没有这名玩家的重连记录。";
                return reply;
            }

            if (string.IsNullOrEmpty(msg.reconnectToken) || msg.reconnectToken != member.ReconnectToken)
            {
                reply.reason = "重连令牌不匹配。";
                return reply;
            }

            if (member.State == ReconnectMemberState.Online)
            {
                reply.reason = "该玩家仍被记录为在线，拒绝重复重连。";
                return reply;
            }
        }
        else
        {
            member = GetOrCreateMember(msg.playerSteamId);
            if (string.IsNullOrEmpty(member.ReconnectToken))
            {
                member.ReconnectToken = Guid.NewGuid().ToString("N");
            }
        }

        reply.reconnectToken = member.ReconnectToken;
        reply.accepted = true;
        reply.reason = msg.reconnectAttempt ? "已接受重连，请等待房主恢复检查点。" : "已登记到重连会话。";
        return reply;
    }

    private void HandleClientHelloReply(ReconnectHelloReplyMessage msg)
    {
        statusLine = msg.accepted ? msg.reason : "重连被拒绝：" + msg.reason;
        if (!msg.accepted)
        {
            ShowSystemMessage(statusLine);
            return;
        }

        clientHelloAccepted = true;
        LobbyInfo lobby = GetLobbyInfo();
        lastSession = new LastSessionRecord
        {
            PlayerSteamId = lobby.LocalSteamId,
            HostSteamId = msg.hostSteamId,
            LobbyId = msg.lobbyId,
            SessionId = msg.sessionId,
            ReconnectToken = msg.reconnectToken,
            SaveSlotId = msg.saveSlotId,
            RunId = msg.runId,
            CurrentFloorId = "",
            CheckpointId = msg.checkpointId,
            CheckpointHash = msg.checkpointHash,
            LastSeenUtc = DateTime.UtcNow
        };
        store.SaveLastSession(lastSession);
    }

    private void HandleServerDisconnect(NetworkConnectionToClient conn)
    {
        if (hostSession == null || conn == null || conn.identity == null)
        {
            return;
        }

        PlayerSpawner spawner = conn.identity.GetComponent<PlayerSpawner>();
        if (spawner == null || spawner.steamID == 0)
        {
            return;
        }

        ReconnectMemberRecord member = GetOrCreateMember(spawner.steamID);
        member.PlayerName = SafePlayerName(spawner);
        member.LastSeenUtc = DateTime.UtcNow;
        member.State = ReconnectMemberState.OfflineReserved;
        member.CurrentFloorId = SafeFloorGuid(spawner);
        member.CheckpointId = hostSession.CurrentCheckpointId;
        SaveHostSession();
        statusLine = "已保留掉线玩家：" + member.SteamId;
    }

    private ReconnectMemberRecord GetOrCreateMember(ulong steamId)
    {
        if (!hostSession.Members.TryGetValue(steamId, out ReconnectMemberRecord member))
        {
            member = new ReconnectMemberRecord
            {
                SteamId = steamId,
                ReconnectToken = Guid.NewGuid().ToString("N"),
                SessionId = hostSession.SessionId,
                SaveSlotId = hostSession.SaveSlotId,
                RunId = hostSession.RunId,
                LastSeenUtc = DateTime.UtcNow,
                State = ReconnectMemberState.Online
            };
            hostSession.Members[steamId] = member;
        }

        return member;
    }

    public static bool TryGetReservedPlayerSlot(ulong steamId, out int slot)
    {
        slot = -1;
        ReconnectController controller = Instance;
        if (controller == null || controller.hostSession == null)
        {
            return false;
        }

        if (!controller.hostSession.Members.TryGetValue(steamId, out ReconnectMemberRecord member))
        {
            return false;
        }

        if (member.PlayerSlot < 0)
        {
            return false;
        }

        slot = member.PlayerSlot;
        return true;
    }

    private void SaveHostSession()
    {
        if (hostSession == null)
        {
            return;
        }

        string signature = BuildHostSessionStateSignature();
        if (signature == lastSavedHostSessionState)
        {
            return;
        }

        hostSession.UpdatedUtc = DateTime.UtcNow;
        store.SaveHostSession(hostSession);
        lastSavedHostSessionState = BuildHostSessionStateSignature();
    }

    private string BuildHostSessionStateSignature()
    {
        if (hostSession == null)
        {
            return "";
        }

        hostSession.Members ??= new Dictionary<ulong, ReconnectMemberRecord>();
        hostSession.Checkpoints ??= new List<ReconnectCheckpointRecord>();

        StringBuilder builder = new StringBuilder();
        builder.Append(hostSession.HostSteamId).Append('|')
            .Append(hostSession.LobbyId).Append('|')
            .Append(hostSession.SessionId).Append('|')
            .Append(hostSession.SaveSlotId).Append('|')
            .Append(hostSession.RunId).Append('|')
            .Append(hostSession.CurrentFloorId).Append('|')
            .Append(hostSession.CurrentCheckpointId).Append('|')
            .Append(hostSession.CurrentCheckpointHash).Append('|')
            .Append(hostSession.Checkpoints.Count);

        foreach (ReconnectCheckpointRecord checkpoint in hostSession.Checkpoints.Where(c => c != null).OrderBy(c => c.CreatedUtc))
        {
            builder.Append('\n')
                .Append(checkpoint.CheckpointId).Append('|')
                .Append(checkpoint.CheckpointHash).Append('|')
                .Append(checkpoint.IsRestoreTarget).Append('|')
                .Append(checkpoint.FloorGuid).Append('|')
                .Append(checkpoint.RunId).Append('|')
                .Append(checkpoint.SaveSlotId).Append('|')
                .Append(checkpoint.CreatedUtc.Ticks);
        }

        foreach (ReconnectMemberRecord member in hostSession.Members.Values.OrderBy(m => m.SteamId))
        {
            builder.Append('\n')
                .Append(member.SteamId).Append('|')
                .Append(member.PlayerName).Append('|')
                .Append(member.PlayerSlot).Append('|')
                .Append(member.ReconnectToken).Append('|')
                .Append(member.SessionId).Append('|')
                .Append(member.SaveSlotId).Append('|')
                .Append(member.RunId).Append('|')
                .Append(member.CurrentFloorId).Append('|')
                .Append(member.CheckpointId).Append('|')
                .Append(member.ModInstalled).Append('|')
                .Append(member.LastHelloUtc.Ticks).Append('|')
                .Append(member.State);
        }

        return builder.ToString();
    }

    private LobbyInfo GetLobbyInfo()
    {
        LobbyInfo info = new LobbyInfo();
        try
        {
            if ((bool)SingletonObject.Find("SteamManager") && App.Initialized)
            {
                info.LocalSteamId = UserData.Me.SteamId;
            }

            GameObject steam = SingletonObject.Find("SteamManager");
            if ((bool)steam && steam.TryGetComponent<LobbyManager>(out var manager))
            {
                info.Manager = manager;
                info.HasLobby = manager.HasLobby;
                if (manager.HasLobby)
                {
                    info.LobbyId = manager.Lobby.SteamId.m_SteamID;
                    info.IsOwner = manager.Lobby.IsOwner;
                    info.HostSteamId = ParseSteamId(manager.Lobby.Owner.user.id.ToString());
                }
            }
        }
        catch
        {
            // Steam can be uninitialized in title/loading windows. Keep defaults.
        }

        if (info.HostSteamId == 0 && NetworkServer.active)
        {
            info.HostSteamId = info.LocalSteamId;
        }

        return info;
    }

    private static ulong ParseSteamId(string text)
    {
        if (ulong.TryParse(text, out ulong value))
        {
            return value;
        }

        return 0;
    }

    private static PlayerSpawner GetLocalSpawner()
    {
        List<PlayerSpawner> players = PlayerSpawner.MultiplayerList;
        if (players == null)
        {
            return null;
        }

        PlayerSpawner fallback = null;
        PlayerSpawner owned = null;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerSpawner player = players[i];
            if (player == null)
            {
                continue;
            }

            fallback ??= player;
            if (player.isLocalPlayer)
            {
                return player;
            }

            if (owned == null && player.isOwned)
            {
                owned = player;
            }
        }

        return owned ?? fallback;
    }

    private string GetSelectedSaveSlot()
    {
        try
        {
            if (OptionsBinding.Instance != null && OptionsBinding.Instance.Options != null)
            {
                return OptionsBinding.Instance.Options.GetString("SelectedProfile", SaveManager.defaultSlotName);
            }
        }
        catch
        {
        }

        return string.IsNullOrEmpty(SaveManager.Binded) ? SaveManager.defaultSlotName : SaveManager.Binded;
    }

    private string GetRunId()
    {
        try
        {
            if (SaveManager.CurrentRun == null)
            {
                return "未开始";
            }

            int seed = SaveManager.CurrentRun.GetInt("Seed", 0);
            int game = SaveManager.CurrentRun.GetInt("CurrentGame", -1);
            int floorCount = SaveManager.CurrentRun.GetInt("FloorCount", 0);
            return seed + ":" + game + ":" + floorCount;
        }
        catch
        {
            return "未知";
        }
    }

    private string GetLocalFloorGuid()
    {
        PlayerSpawner spawner = GetLocalSpawner();
        return SafeFloorGuid(spawner);
    }

    private static string SafeFloorGuid(PlayerSpawner spawner)
    {
        try
        {
            return spawner != null && spawner.PlayerAvatar != null ? spawner.PlayerAvatar.currentFloorGuid : "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafePlayerName(PlayerSpawner spawner)
    {
        try
        {
            if (spawner != null && spawner.PlayerAvatar != null)
            {
                return spawner.PlayerAvatar.Name;
            }
        }
        catch
        {
        }

        return "";
    }

    private static string SafeLocalPlayerName()
    {
        PlayerSpawner spawner = GetLocalSpawner();
        string name = SafePlayerName(spawner);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        try
        {
            return UserData.Me.Name;
        }
        catch
        {
            return "";
        }
    }

    private static FloorData FindFloor(string guid)
    {
        try
        {
            if (DungeonManager.Instance != null && !string.IsNullOrEmpty(guid)
                && DungeonManager.Instance.generatedFloors.TryGetValue(guid, out FloorData floor))
            {
                return floor;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ComputeSaveHash(SaveData saveData)
    {
        StringBuilder builder = new StringBuilder();
        foreach (KeyValuePair<string, object> pair in saveData.BakedData.OrderBy(p => p.Key))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value == null ? "<null>" : pair.Value.ToString()).Append('\n');
        }

        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static string ShortGuid()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    private static string Short(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "-";
        }

        return text.Length <= 12 ? text : text.Substring(0, 12);
    }

    private static string NullToDash(string text)
    {
        return string.IsNullOrEmpty(text) ? "-" : text;
    }

    private static string ShortPath(string path)
    {
        return string.IsNullOrEmpty(path) ? "未启用" : Path.GetFileName(path);
    }

    private string DescribeRole()
    {
        if (NetworkServer.active && NetworkClient.active)
        {
            return "房主";
        }

        if (NetworkClient.active)
        {
            return "客户端";
        }

        return "离线/标题";
    }

    private static string DescribeMemberState(ReconnectMemberState state)
    {
        return state switch
        {
            ReconnectMemberState.Online => "在线",
            ReconnectMemberState.OfflineReserved => "掉线保留",
            ReconnectMemberState.ReconnectPending => "等待重连",
            ReconnectMemberState.ReconnectedWaiting => "已回房待恢复",
            ReconnectMemberState.ExitedGracefully => "主动退出",
            ReconnectMemberState.Removed => "已移除",
            _ => state.ToString()
        };
    }

    private static string DescribeMemberMod(ReconnectMemberRecord member)
    {
        if (member == null)
        {
            return "mod未知";
        }

        return member.ModInstalled ? "mod已确认" : "mod未握手";
    }

    private static string ReadLobbySessionId(LobbyInfo lobby)
    {
        try
        {
            if (lobby.HasLobby && lobby.Manager != null)
            {
                return lobby.Manager.Lobby["ReconnectSessionId"];
            }
        }
        catch
        {
        }

        return "";
    }

    private static void ShowSystemMessage(string message)
    {
        try
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.GetElement<UI_SystemMessage>().Open(message, 3f);
                return;
            }
        }
        catch
        {
        }

        Debug.Log("[SephiriaReconnect] " + message);
    }

    private struct LobbyInfo
    {
        public LobbyManager Manager;
        public bool HasLobby;
        public bool IsOwner;
        public ulong LocalSteamId;
        public ulong HostSteamId;
        public ulong LobbyId;
    }

    private sealed class LobbySettingsSnapshot
    {
        public string Name;
        public string GameVersion;
        public ELobbyType Type;
        public int MaxMembers;
        public string Chapter;
    }

    private sealed class PanelActionButton
    {
        public RectTransform Rect;
        public UnityEngine.Events.UnityAction Action;
        public Image Image;
        public Text Label;
    }

    private sealed class ClickRelay : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private UnityEngine.Events.UnityAction action;
        private Action<bool> hoverAction;

        public void Initialize(UnityEngine.Events.UnityAction clickAction)
        {
            action = clickAction;
        }

        public void SetHoverAction(Action<bool> onHover)
        {
            hoverAction = onHover;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                action?.Invoke();
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hoverAction?.Invoke(true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hoverAction?.Invoke(false);
        }
    }
}
