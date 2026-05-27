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

    private static string restoreGenerationFloorGuid = "";
    private static int restoreGenerationPlayerCount;
    private static int restoreGenerationMaxLuck;
    private static float restoreGenerationUntil;

    private ReconnectStore store;
    private ReconnectConfig config;
    private ReconnectSessionRecord hostSession;
    private LastSessionRecord lastSession;

    private bool panelOpen;
    private Canvas uiCanvas;
    private GameObject iconObject;
    private Image iconImage;
    private UI_HorayButton iconButton;
    private CanvasGroup iconCanvasGroup;
    private Text iconText;
    private Sprite iconSprite;
    private Sprite iconHoverSprite;
    private Sprite iconOfflineSprite;
    private Sprite iconOfflineHoverSprite;
    private Sprite iconDisabledSprite;
    private GameObject panelObject;
    private Image panelImage;
    private Text panelText;
    private RectTransform iconRectTransform;
    private RectTransform panelRectTransform;
    private readonly List<PanelActionButton> panelButtons = new List<PanelActionButton>();
    private Font uiFont;
    private UI_HUDMenu cachedHudMenu;
    private Transform iconHudParent;
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
    private float nextPanelMemberRefreshAt;
    private float nextPanelLobbyMemberRefreshAt;
    private float nextPanelTextRefreshAt;
    private float nextGameStyleProbeAt;
    private bool lastHudVisible;
    private string lastPublishedLobbyState = "";
    private string lastSavedHostSessionState = "";
    private int autoHelloAttempts;
    private bool clientHelloAccepted;
    private bool wasServerActive;
    private bool serverEventsAttached;
    private bool serverMessageHandlerRegistered;
    private bool clientMessageHandlerRegistered;
    private bool wasClientActive;
    private readonly Dictionary<int, ulong> connectionSteamIds = new Dictionary<int, ulong>();
    private string lastObservedFloorGuid = "";
    private string statusLine = "正在初始化。";
    private LobbySettingsSnapshot pendingLobbySettings;
    private bool restoreHostRestartInProgress;
    private float reconnectConnectionOpenUntil;
    private bool reconnectJoinWindowOpen;
    private bool reconnectPreviousAllowConnection;
    private bool reconnectPreviousAccessDenyInDungeon;
    private static ulong lastReconnectLobbyConnectAttemptId;
    private static float lastReconnectLobbyConnectAttemptAt;
    private static readonly Vector3[] ScreenRectCorners = new Vector3[4];
    private static readonly MethodInfo DungeonSaveCurrentSessionDataMethod =
        typeof(DungeonManager).GetMethod("SaveCurrentSessionData", BindingFlags.Instance | BindingFlags.NonPublic);

    private void Awake()
    {
        if (Instance != null && !ReferenceEquals(Instance, this))
        {
            ReconnectLogger.Warning("Duplicate controller detected, destroying the new instance.");
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
        ReconnectMessageSerializers.Register();
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
        ClearRestoreGenerationOverride("controller-destroyed");
        DestroyUi();
        if (wasActiveInstance)
        {
            ReconnectLogger.Shutdown();
        }
    }

    private void Update()
    {
        ReconnectLogger.Tick();
        bool shouldShowUi = ShouldShowReconnectUi();
        if (shouldShowUi)
        {
            if (uiCanvas == null)
            {
                EnsureUi();
            }
        }
        else if (uiCanvas != null)
        {
            DestroyUi();
        }

        if (uiCanvas != null)
        {
            UpdateHudVisibility();
        }

        TickRestoreGenerationOverride();

        if (Time.unscaledTime < nextProbeAt)
        {
            return;
        }

        nextProbeAt = Time.unscaledTime + 1f;
        TrackConnectionDrop();
        TrackHostSession();
        RegisterMessageHandlers();
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

        bool interactive = visible && ShouldAllowReconnectIconInteraction();
        EnsureIconCanvasGroup();
        if (iconImage != null && iconImage.raycastTarget != interactive)
        {
            iconImage.raycastTarget = interactive;
        }

        if (iconCanvasGroup != null)
        {
            iconCanvasGroup.ignoreParentGroups = true;
            iconCanvasGroup.alpha = 1f;
            iconCanvasGroup.blocksRaycasts = interactive;
            iconCanvasGroup.interactable = interactive;
        }

        if (iconButton != null && iconButton.interactable != visible)
        {
            iconButton.interactable = visible;
        }

        if (!interactive && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == iconObject)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }

        if (!interactive && iconHovered)
        {
            iconHovered = false;
            ApplyReconnectIconVisual();
        }

        if ((!visible || !interactive) && panelOpen)
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

                // Keep the icon visible while native panels are open. The native lower-left
                // icons remain visible under the panel shade, but they stop accepting input.
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

    private static bool ShouldAllowReconnectIconInteraction()
    {
        try
        {
            return UIManager.Instance == null
                || UIManager.Instance.CurrentControlStack == null
                || UIManager.Instance.CurrentControlStack.Count <= 0;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureIconCanvasGroup()
    {
        if (iconObject == null)
        {
            iconCanvasGroup = null;
            return;
        }

        if (iconCanvasGroup == null || iconCanvasGroup.gameObject != iconObject)
        {
            iconCanvasGroup = iconObject.GetComponent<CanvasGroup>();
            if (iconCanvasGroup == null)
            {
                iconCanvasGroup = iconObject.AddComponent<CanvasGroup>();
            }
        }

        iconCanvasGroup.ignoreParentGroups = true;
        iconCanvasGroup.alpha = 1f;
    }

    private UI_HUDMenu GetCachedHudMenu()
    {
        if (cachedHudMenu == null || !cachedHudMenu.gameObject.activeInHierarchy || !HasCoreHudButtons(cachedHudMenu))
        {
            cachedHudMenu = FindBestHudMenu();
            UpdateUiCanvasSortingOrder();
        }

        return cachedHudMenu;
    }

    private static UI_HUDMenu FindBestHudMenu()
    {
        UI_HUDMenu[] menus = FindObjectsByType<UI_HUDMenu>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        UI_HUDMenu fallback = null;
        for (int i = 0; i < menus.Length; i++)
        {
            UI_HUDMenu menu = menus[i];
            if (menu == null || !menu.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (HasCoreHudButtons(menu))
            {
                return menu;
            }

            fallback ??= menu;
        }

        return fallback;
    }

    private static bool HasCoreHudButtons(UI_HUDMenu hudMenu)
    {
        return hudMenu != null && hudMenu.inventoryButton != null && hudMenu.statsButton != null;
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
        iconText = null;

        LoadReconnectIconSprites();
        EnsureReconnectIconInHudMenu();

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
        panelRectTransform.anchoredPosition = new Vector2(config.IconOffsetX, config.IconOffsetY + GetIconSize() + 10f);

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

        DestroyIconSprites();
        uiCanvas = null;
        iconObject = null;
        iconImage = null;
        iconButton = null;
        iconCanvasGroup = null;
        iconText = null;
        iconSprite = null;
        iconHoverSprite = null;
        iconOfflineSprite = null;
        iconOfflineHoverSprite = null;
        iconDisabledSprite = null;
        iconHovered = false;
        iconRectTransform = null;
        iconHudParent = null;
        panelRectTransform = null;
        panelObject = null;
        panelImage = null;
        panelText = null;
        panelButtons.Clear();
        gameStyleApplied = false;
        cachedHudMenu = null;
        lastHudVisible = false;
    }

    private void DestroyIconSprites()
    {
        HashSet<int> destroyedSprites = new HashSet<int>();
        HashSet<int> destroyedTextures = new HashSet<int>();
        DestroyIconSprite(iconSprite, destroyedSprites, destroyedTextures);
        DestroyIconSprite(iconHoverSprite, destroyedSprites, destroyedTextures);
        DestroyIconSprite(iconOfflineSprite, destroyedSprites, destroyedTextures);
        DestroyIconSprite(iconOfflineHoverSprite, destroyedSprites, destroyedTextures);
        DestroyIconSprite(iconDisabledSprite, destroyedSprites, destroyedTextures);
    }

    private static void DestroyIconSprite(Sprite sprite, HashSet<int> destroyedSprites, HashSet<int> destroyedTextures)
    {
        if (sprite == null)
        {
            return;
        }

        Texture2D texture = sprite.texture;
        int spriteId = sprite.GetInstanceID();
        if (destroyedSprites.Add(spriteId))
        {
            Destroy(sprite);
        }

        if (texture != null)
        {
            int textureId = texture.GetInstanceID();
            if (destroyedTextures.Add(textureId))
            {
                Destroy(texture);
            }
        }
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
        UpdatePanelPositionNearIcon();
        if (panelObject != null)
        {
            panelObject.SetActive(panelOpen);
        }

        if (panelOpen)
        {
            nextPanelTextRefreshAt = 0f;
            RefreshPanelMemberData(force: true);
        }

        if (panelOpen && EventSystem.current != null && iconObject != null)
        {
            EventSystem.current.SetSelectedGameObject(iconObject);
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
        if (Time.unscaledTime < nextIconPlacementProbeAt)
        {
            return;
        }

        nextIconPlacementProbeAt = Time.unscaledTime + 1f;
        EnsureReconnectIconInHudMenu();
        UpdatePanelPositionNearIcon();
    }

    private void EnsureReconnectIconInHudMenu()
    {
        UI_HUDMenu hudMenu = GetCachedHudMenu();
        if (hudMenu == null || !hudMenu.gameObject.activeInHierarchy)
        {
            return;
        }

        Transform hudParent = hudMenu.transform;
        if (iconObject != null && iconHudParent == hudParent && iconObject.transform.parent == hudParent)
        {
            bool changed = EnsureReconnectIconSiblingIsLast(hudParent);
            changed |= ConfigureReconnectIconRect(hudMenu);
            if (changed)
            {
                UpdatePanelPositionNearIcon();
            }

            return;
        }

        if (iconObject != null)
        {
            Destroy(iconObject);
            iconObject = null;
            iconImage = null;
            iconButton = null;
            iconRectTransform = null;
        }

        DestroyDuplicateReconnectIcons(hudParent);
        iconObject = CreateReconnectIconButton(hudParent, GetNativeHudIconSize(hudMenu));
        iconHudParent = hudParent;
        iconImage = iconObject.GetComponent<Image>();
        iconRectTransform = iconObject.GetComponent<RectTransform>();

        iconButton = iconObject.GetComponent<UI_HorayButton>();
        if (iconButton != null)
        {
            iconButton.onClick = new Button.ButtonClickedEvent();
            iconButton.onClick.AddListener(TogglePanel);
            iconButton.allowSubmitInput = false;
        }

        ClickRelay clickRelay = iconObject.GetComponent<ClickRelay>() ?? iconObject.AddComponent<ClickRelay>();
        clickRelay.SetHoverAction(SetIconHover);
        ConfigureReconnectIconRect(hudMenu);
        ApplyReconnectIconVisual();
        UpdateUiCanvasSortingOrder();
        UpdatePanelPositionNearIcon();
    }

    private static void DestroyDuplicateReconnectIcons(Transform hudParent)
    {
        if (hudParent == null)
        {
            return;
        }

        for (int i = hudParent.childCount - 1; i >= 0; i--)
        {
            Transform child = hudParent.GetChild(i);
            if (child != null && child.name == "ReconnectIcon")
            {
                Destroy(child.gameObject);
            }
        }
    }

    private bool EnsureReconnectIconSiblingIsLast(Transform hudParent)
    {
        if (iconObject == null || hudParent == null)
        {
            return false;
        }

        int lastIndex = hudParent.childCount - 1;
        if (lastIndex < 0 || iconObject.transform.GetSiblingIndex() == lastIndex)
        {
            return false;
        }

        iconObject.transform.SetAsLastSibling();
        return true;
    }

    private bool ConfigureReconnectIconRect(UI_HUDMenu hudMenu)
    {
        if (iconRectTransform == null)
        {
            return false;
        }

        bool changed = false;
        if (!Approximately(iconRectTransform.anchorMin, Vector2.zero))
        {
            iconRectTransform.anchorMin = Vector2.zero;
            changed = true;
        }

        if (!Approximately(iconRectTransform.anchorMax, Vector2.zero))
        {
            iconRectTransform.anchorMax = Vector2.zero;
            changed = true;
        }

        if (!Approximately(iconRectTransform.pivot, Vector2.zero))
        {
            iconRectTransform.pivot = Vector2.zero;
            changed = true;
        }

        if (!Approximately(iconRectTransform.localScale, Vector3.one))
        {
            iconRectTransform.localScale = Vector3.one;
            changed = true;
        }

        if (iconRectTransform.localRotation != Quaternion.identity)
        {
            iconRectTransform.localRotation = Quaternion.identity;
            changed = true;
        }

        if (!Approximately(iconRectTransform.anchoredPosition, Vector2.zero))
        {
            iconRectTransform.anchoredPosition = Vector2.zero;
            changed = true;
        }

        Vector2 desiredSize = GetNativeHudIconSize(hudMenu);
        if (!Approximately(iconRectTransform.sizeDelta, desiredSize))
        {
            iconRectTransform.sizeDelta = desiredSize;
            changed = true;
        }

        changed |= EnsureReconnectIconSiblingIsLast(iconRectTransform.parent);
        return changed;
    }

    private static bool Approximately(Vector2 current, Vector2 desired)
    {
        return Vector2.SqrMagnitude(current - desired) <= 0.0001f;
    }

    private static bool Approximately(Vector3 current, Vector3 desired)
    {
        return Vector3.SqrMagnitude(current - desired) <= 0.0001f;
    }

    private static Vector2 GetNativeHudIconSize(UI_HUDMenu hudMenu)
    {
        float height = 21f;
        if (hudMenu != null)
        {
            height = Mathf.Max(
                GetHudButtonSize(hudMenu.inventoryButton).y,
                GetHudButtonSize(hudMenu.statsButton).y,
                GetHudButtonSize(hudMenu.passiveButton).y,
                GetHudButtonSize(hudMenu.presetButton).y);
        }

        if (height <= 0f)
        {
            height = 21f;
        }

        return new Vector2(height, height);
    }

    private static Vector2 GetHudButtonSize(UI_HorayButton button)
    {
        RectTransform rect = button != null ? button.transform as RectTransform : null;
        return rect != null ? rect.sizeDelta : Vector2.zero;
    }

    private void UpdatePanelPositionNearIcon()
    {
        if (panelRectTransform == null)
        {
            return;
        }

        if (!TryGetHudClusterScreenRect(out Rect anchorRect))
        {
            Canvas iconCanvas = iconObject != null ? iconObject.GetComponentInParent<Canvas>() : null;
            if (iconCanvas == null || iconRectTransform == null || !TryGetScreenRect(iconRectTransform, iconCanvas, out anchorRect))
            {
                return;
            }
        }

        Vector2 panelSize = panelRectTransform.sizeDelta;
        const float margin = 4f;
        float maxX = Mathf.Max(margin, Screen.width - panelSize.x - margin);
        float maxY = Mathf.Max(margin, Screen.height - panelSize.y - margin);
        float x = maxX <= margin ? margin : Mathf.Clamp(anchorRect.xMin, margin, maxX);
        float y = maxY <= margin ? margin : Mathf.Clamp(anchorRect.yMax + 10f, margin, maxY);
        panelRectTransform.anchoredPosition = new Vector2(x, y);
    }

    private bool TryGetHudClusterScreenRect(out Rect clusterRect)
    {
        clusterRect = default;
        bool found = false;
        UI_HUDMenu hudMenu = GetCachedHudMenu();
        Canvas canvas = hudMenu != null ? hudMenu.GetComponentInParent<Canvas>() : null;
        if (canvas == null)
        {
            return false;
        }

        TryIncludeHudButtonScreenRect(hudMenu.inventoryButton, canvas, ref clusterRect, ref found);
        TryIncludeHudButtonScreenRect(hudMenu.statsButton, canvas, ref clusterRect, ref found);
        TryIncludeHudButtonScreenRect(hudMenu.passiveButton, canvas, ref clusterRect, ref found);
        TryIncludeHudButtonScreenRect(hudMenu.presetButton, canvas, ref clusterRect, ref found);
        if (iconRectTransform != null && iconRectTransform.gameObject.activeInHierarchy && TryGetScreenRect(iconRectTransform, canvas, out Rect iconRect))
        {
            IncludeScreenRect(ref clusterRect, ref found, iconRect);
        }

        return found;
    }

    private static void TryIncludeHudButtonScreenRect(UI_HorayButton button, Canvas canvas, ref Rect clusterRect, ref bool found)
    {
        RectTransform rect = button != null && button.gameObject.activeInHierarchy ? button.transform as RectTransform : null;
        if (rect != null && TryGetScreenRect(rect, canvas, out Rect screenRect))
        {
            IncludeScreenRect(ref clusterRect, ref found, screenRect);
        }
    }

    private static void IncludeScreenRect(ref Rect unionRect, ref bool found, Rect screenRect)
    {
        unionRect = found ? Rect.MinMaxRect(
            Mathf.Min(unionRect.xMin, screenRect.xMin),
            Mathf.Min(unionRect.yMin, screenRect.yMin),
            Mathf.Max(unionRect.xMax, screenRect.xMax),
            Mathf.Max(unionRect.yMax, screenRect.yMax)) : screenRect;
        found = true;
    }

    private bool TryAttachIconAfterLowerLeftHudCluster()
    {
        bool found = false;
        Rect unionRect = default;

        TryIncludeHudMenuButtonsInCluster(ref unionRect, ref found);
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
        UpdatePanelPositionNearIcon();

        if (panelText == null || !panelOpen)
        {
            return;
        }

        if (Time.unscaledTime < nextPanelTextRefreshAt)
        {
            return;
        }

        nextPanelTextRefreshAt = Time.unscaledTime + 2f;
        RefreshPanelMemberData(force: false);
        LobbyInfo lobby = GetLobbyInfo();
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("状态：" + statusLine);
        builder.AppendLine();
        builder.AppendLine("身份：" + DescribeRole());
        builder.AppendLine("存档槽：" + GetSelectedSaveSlot());
        builder.AppendLine("局内标识：" + GetRunId());
        builder.AppendLine("网络：服务端=" + NetworkServer.active + "，客户端=" + NetworkClient.active);
        builder.AppendLine("房间：" + (lobby.HasLobby ? lobby.LobbyId.ToString() : "无"));
        builder.AppendLine("日志：" + ShortPath(ReconnectLogger.CurrentLogPath));
        if (NetworkServer.active && hostSession != null)
        {
            builder.AppendLine();
            builder.AppendLine("会话：" + hostSession.SessionId);
            builder.AppendLine("检查点：" + NullToDash(hostSession.CurrentCheckpointId));
            builder.AppendLine("检查点哈希：" + Short(hostSession.CurrentCheckpointHash));
            List<ReconnectMemberRecord> members = GetPanelMembers();
            builder.AppendLine("联机成员：" + members.Count);
            if (members.Count == 0)
            {
                builder.AppendLine("  暂无已记录成员");
            }

            foreach (ReconnectMemberRecord member in members)
            {
                builder.AppendLine("  " + FormatPanelMember(member));
            }
        }
        else if (NetworkClient.active && !NetworkServer.active)
        {
            builder.AppendLine();
            string lobbySessionId = ReadLobbySessionId(lobby);
            builder.AppendLine("房主会话：" + Short(!string.IsNullOrEmpty(lobbySessionId) ? lobbySessionId : lastSession?.SessionId));
            builder.AppendLine("联机成员：由房主面板显示完整列表");
            builder.AppendLine("本机握手：" + (clientHelloAccepted ? "已确认" : "未确认"));
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
        builder.AppendLine("流程：保存楼层入口点 -> 房主先本层恢复 -> 邀请成员回房 -> 成员握手。");
        builder.AppendLine("本层恢复点只在当前会话内保留；进入新局会清理旧检查点。");

        string text = builder.ToString();
        ApplyPanelSizeForText(text);
        panelText.text = text;
    }

    private void ApplyPanelSizeForText(string text)
    {
        if (panelRectTransform == null || string.IsNullOrEmpty(text))
        {
            return;
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int lineCount = Mathf.Max(1, lines.Length);
        int maxUnits = 0;
        foreach (string line in lines)
        {
            maxUnits = Mathf.Max(maxUnits, EstimatePanelTextUnits(line));
        }

        float maxWidth = Mathf.Max(460f, Screen.width - 24f);
        float maxHeight = Mathf.Max(360f, Screen.height - 24f);
        float desiredWidth = Mathf.Clamp(460f + Mathf.Max(0, maxUnits - 42) * 6.5f, 460f, maxWidth);
        float desiredHeight = Mathf.Clamp(120f + lineCount * 17f, 360f, maxHeight);
        Vector2 desired = new Vector2(desiredWidth, desiredHeight);
        if (Vector2.Distance(panelRectTransform.sizeDelta, desired) > 0.5f)
        {
            panelRectTransform.sizeDelta = desired;
            UpdatePanelPositionNearIcon();
        }
    }

    private static int EstimatePanelTextUnits(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return 0;
        }

        int units = 0;
        foreach (char ch in line)
        {
            units += ch > 127 ? 2 : 1;
        }

        return units;
    }

    private void RefreshPanelMemberData(bool force)
    {
        if (!NetworkServer.active || hostSession == null)
        {
            return;
        }

        hostSession.Members ??= new Dictionary<ulong, ReconnectMemberRecord>();
        if (!force && Time.unscaledTime < nextPanelMemberRefreshAt)
        {
            return;
        }

        nextPanelMemberRefreshAt = Time.unscaledTime + 3f;
        RefreshHostMembersFromPlayerSpawners();
        if (force || Time.unscaledTime >= nextPanelLobbyMemberRefreshAt)
        {
            nextPanelLobbyMemberRefreshAt = Time.unscaledTime + Mathf.Max(10f, config.HostLobbyRefreshSeconds);
            RefreshHostMembersFromLobby();
        }

        SaveHostSession();
    }

    private List<ReconnectMemberRecord> GetPanelMembers()
    {
        if (hostSession?.Members == null)
        {
            return new List<ReconnectMemberRecord>();
        }

        return hostSession.Members.Values
            .Where(member => member != null && member.State != ReconnectMemberState.Removed)
            .OrderBy(member => member.PlayerSlot < 0 ? int.MaxValue : member.PlayerSlot)
            .ThenBy(member => GetMemberDisplayName(member), StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.SteamId)
            .ToList();
    }

    private static string FormatPanelMember(ReconnectMemberRecord member)
    {
        string slot = member.PlayerSlot >= 0 ? member.PlayerSlot.ToString() : "?";
        string playerName = !string.IsNullOrWhiteSpace(member?.PlayerName) ? member.PlayerName.Trim() : "未知";
        string steamName = !string.IsNullOrWhiteSpace(member?.SteamName) ? member.SteamName.Trim() : "未知";
        return "槽位 " + slot + "  游戏名 " + playerName + "  Steam昵称 " + steamName + "  " + DescribeMemberState(member.State) + "  " + DescribeMemberMod(member);
    }

    private static string GetMemberDisplayName(ReconnectMemberRecord member)
    {
        if (member == null)
        {
            return "未知成员";
        }

        if (!string.IsNullOrWhiteSpace(member.PlayerName))
        {
            return member.PlayerName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(member.SteamName))
        {
            return member.SteamName.Trim();
        }

        return "未知成员";
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

    private GameObject CreateReconnectIconButton(Transform parent, Vector2 size)
    {
        GameObject buttonObject = new GameObject("ReconnectIcon");
        buttonObject.transform.SetParent(parent, false);

        Image image = buttonObject.AddComponent<Image>();
        image.raycastTarget = true;
        image.color = Color.white;
        image.preserveAspect = true;

        iconCanvasGroup = buttonObject.AddComponent<CanvasGroup>();
        iconCanvasGroup.ignoreParentGroups = true;
        iconCanvasGroup.blocksRaycasts = true;
        iconCanvasGroup.interactable = true;
        iconCanvasGroup.alpha = 1f;

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = size;

        LayoutElement layoutElement = buttonObject.AddComponent<LayoutElement>();
        layoutElement.minWidth = size.x;
        layoutElement.minHeight = size.y;
        layoutElement.preferredWidth = size.x;
        layoutElement.preferredHeight = size.y;
        layoutElement.flexibleWidth = 0f;
        layoutElement.flexibleHeight = 0f;

        UI_HorayButton button = buttonObject.AddComponent<UI_HorayButton>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.SpriteSwap;
        button.allowSubmitInput = false;

        Navigation navigation = button.navigation;
        navigation.mode = Navigation.Mode.None;
        button.navigation = navigation;

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

        if (Time.unscaledTime < nextGameStyleProbeAt)
        {
            return;
        }

        nextGameStyleProbeAt = Time.unscaledTime + 5f;
        bool appliedAny = false;
        UI_HUDMenu hudMenu = GetCachedHudMenu();
        UI_HorayButton styleButton = GetStyleButton(hudMenu);
        if (styleButton != null)
        {
            Image sourceImage = styleButton.targetGraphic as Image ?? styleButton.GetComponent<Image>();
            if (sourceImage != null)
            {
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

        Sprite desiredSprite = SelectReconnectIconSprite();
        if (iconImage.sprite != desiredSprite)
        {
            iconImage.sprite = desiredSprite;
        }

        if (iconImage.type != Image.Type.Simple)
        {
            iconImage.type = Image.Type.Simple;
        }

        if (!iconImage.preserveAspect)
        {
            iconImage.preserveAspect = true;
        }

        if (iconImage.material != null)
        {
            iconImage.material = null;
        }

        if (iconImage.color != Color.white)
        {
            iconImage.color = Color.white;
        }

        if (iconButton != null)
        {
            Sprite normalSprite = iconImage.sprite != null ? iconImage.sprite : iconSprite;
            Sprite selectedSprite = SelectReconnectIconSelectedSprite(normalSprite);
            SpriteState state = iconButton.spriteState;
            Sprite disabledSprite = iconDisabledSprite != null ? iconDisabledSprite : normalSprite;
            if (state.highlightedSprite != selectedSprite ||
                state.pressedSprite != selectedSprite ||
                state.selectedSprite != selectedSprite ||
                state.disabledSprite != disabledSprite)
            {
                state.highlightedSprite = selectedSprite;
                state.pressedSprite = selectedSprite;
                state.selectedSprite = selectedSprite;
                state.disabledSprite = disabledSprite;
                iconButton.spriteState = state;
            }

            if (iconButton.targetGraphic != iconImage)
            {
                iconButton.targetGraphic = iconImage;
            }

            if (iconButton.transition != Selectable.Transition.SpriteSwap)
            {
                iconButton.transition = Selectable.Transition.SpriteSwap;
            }

            if (!iconButton.interactable)
            {
                iconButton.interactable = true;
            }

            if (iconButton.allowSubmitInput)
            {
                iconButton.allowSubmitInput = false;
            }
        }

        if (iconRectTransform != null && !Approximately(iconRectTransform.localScale, Vector3.one))
        {
            iconRectTransform.localScale = Vector3.one;
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

    private Sprite SelectReconnectIconSelectedSprite(Sprite fallback)
    {
        if (HasOfflineReservedMember() && iconOfflineHoverSprite != null)
        {
            return iconOfflineHoverSprite;
        }

        if (iconHoverSprite != null)
        {
            return iconHoverSprite;
        }

        return fallback;
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
        iconSprite = LoadReconnectIconSprite("reconnect-icon-normal.png") ?? CreateReconnectIconSprite(hover: false);
        iconHoverSprite = LoadReconnectIconSprite("reconnect-icon-selected.png") ?? CreateReconnectIconSprite(hover: true);
        iconOfflineSprite = LoadReconnectIconSprite("reconnect-icon-offline.png") ?? iconSprite;
        iconOfflineHoverSprite = LoadReconnectIconSprite("reconnect-icon-offline-selected.png") ?? iconHoverSprite;
        iconDisabledSprite = LoadReconnectIconSprite("reconnect-icon-disabled.png") ?? iconSprite;
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
            ReconnectLogger.Warning("Failed to load icon asset " + fileName + ": " + ex.Message);
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
        if (NetworkServer.active)
        {
            statusLine = "房主不需要发送握手；请让成员更新 Mod 后重新加入或点击成员端握手。";
            ReconnectLogger.Info("Hello button ignored on host.");
            ShowSystemMessage(statusLine);
            return;
        }

        SendHello(ShouldAttemptReconnectHello());
    }

    private void OpenSteamReconnectInvite()
    {
        LobbyInfo lobby = GetLobbyInfo();
        ReconnectLogger.Info("Invite requested. " + DescribeLobby(lobby) + " hostSession=" + (hostSession != null) + " members=" + (hostSession?.Members?.Count ?? 0));
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
                    ReconnectLogger.Info("No Steam lobby found; creating lobby before invite. " + DescribeLobby(lobby));
                    lobby.Manager.Create();
                    StartCoroutine(OpenSteamReconnectInviteWhenLobbyReady());
                    statusLine = "正在创建 Steam 房间，随后打开邀请窗口。";
                    ShowSystemMessage(statusLine);
                }
                catch (Exception ex)
                {
                    statusLine = "创建 Steam 房间失败：" + ex.Message;
                    ReconnectLogger.Error("Create lobby for invite failed: " + ex);
                }
                return;
            }

            statusLine = "当前没有可邀请的 Steam 房间。";
            ReconnectLogger.Warning("Invite aborted because no Steam lobby is available. " + DescribeLobby(lobby));
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
                ReconnectLogger.Info("Created Steam lobby is ready for invite. " + DescribeLobby(lobby));
                ApplyLobbySettingsSnapshot(lobby);
                OpenSteamInviteForLobby(lobby.LobbyId);
                yield break;
            }

            yield return null;
        }

        statusLine = "创建 Steam 房间超时，无法打开邀请窗口。";
        ReconnectLogger.Warning("Timed out waiting for Steam lobby before invite.");
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
            ReconnectLogger.Info("Steam invite overlay opened. lobby=" + lobbyId + " targetedInvites=" + sent);
            ShowSystemMessage(statusLine);
        }
        catch (Exception ex)
        {
            statusLine = "打开 Steam 邀请窗口失败：" + ex.Message;
            ReconnectLogger.Error("Open invite dialog failed: " + ex);
        }
    }

    private int InviteRecordedDisconnectedPlayers(ulong lobbyId)
    {
        if (hostSession == null || lobbyId == 0)
        {
            ReconnectLogger.Info("No recorded member invite targets. hostSession=" + (hostSession != null) + " lobby=" + lobbyId);
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
                ReconnectLogger.Info("Skipped invite target because member is not reserved. " + DescribeMember(member));
                continue;
            }

            try
            {
                bool invited = TryInviteUserToLobby(lobbyId, member.SteamId);
                if (!invited)
                {
                    invited = SteamFriends.InviteUserToGame(new CSteamID(member.SteamId), connectString);
                    if (invited)
                    {
                        ReconnectLogger.Info("Sent fallback Steam game invite. " + DescribeMember(member) + " lobby=" + lobbyId);
                    }
                }

                if (invited)
                {
                    sent++;
                    member.State = ReconnectMemberState.ReconnectPending;
                    ReconnectLogger.Info("Sent targeted Steam invite. " + DescribeMember(member) + " lobby=" + lobbyId);
                }
                else
                {
                    ReconnectLogger.Warning("Steam targeted invite returned false. " + DescribeMember(member) + " lobby=" + lobbyId);
                }
            }
            catch (Exception ex)
            {
                ReconnectLogger.Warning("Failed to invite " + member.SteamId + ": " + ex.Message);
            }
        }

        if (sent > 0)
        {
            SaveHostSession();
        }

        return sent;
    }

    private static bool TryInviteUserToLobby(ulong lobbyId, ulong steamId)
    {
        try
        {
            bool invited = SteamMatchmaking.InviteUserToLobby(new CSteamID(lobbyId), new CSteamID(steamId));
            if (invited)
            {
                ReconnectLogger.Info("Sent Steam lobby invite. steam=" + steamId + " lobby=" + lobbyId);
            }
            else
            {
                ReconnectLogger.Warning("Steam InviteUserToLobby returned false. steam=" + steamId + " lobby=" + lobbyId);
            }

            return invited;
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Steam InviteUserToLobby failed. steam=" + steamId + " lobby=" + lobbyId + " error=" + ex.Message);
            return false;
        }
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
        RegisterServerMessageHandlers();
        RegisterClientMessageHandlers();
    }

    private void UnregisterMessageHandlers()
    {
        UnregisterServerMessageHandlers();
        UnregisterClientMessageHandlers();
    }

    private void RegisterServerMessageHandlers()
    {
        if (!NetworkServer.active)
        {
            serverMessageHandlerRegistered = false;
            return;
        }

        if (serverMessageHandlerRegistered)
        {
            return;
        }

        try
        {
            NetworkServer.RegisterHandler<ReconnectHelloMessage>(HandleServerHello, false);
            serverMessageHandlerRegistered = true;
            ReconnectLogger.Info("Mirror server hello handler registered.");
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Server hello handler registration delayed: " + ex.Message);
        }
    }

    private void RegisterClientMessageHandlers()
    {
        if (!NetworkClient.active)
        {
            clientMessageHandlerRegistered = false;
            return;
        }

        if (clientMessageHandlerRegistered)
        {
            return;
        }

        try
        {
            NetworkClient.RegisterHandler<ReconnectHelloReplyMessage>(HandleClientHelloReply, false);
            clientMessageHandlerRegistered = true;
            ReconnectLogger.Info("Mirror client hello-reply handler registered.");
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Client hello-reply handler registration delayed: " + ex.Message);
        }
    }

    private void UnregisterServerMessageHandlers()
    {
        if (!serverMessageHandlerRegistered)
        {
            return;
        }

        try
        {
            NetworkServer.UnregisterHandler<ReconnectHelloMessage>();
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to unregister server hello handler: " + ex.Message);
        }

        serverMessageHandlerRegistered = false;
    }

    private void UnregisterClientMessageHandlers()
    {
        if (!clientMessageHandlerRegistered)
        {
            return;
        }

        try
        {
            NetworkClient.UnregisterHandler<ReconnectHelloReplyMessage>();
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to unregister client hello-reply handler: " + ex.Message);
        }

        clientMessageHandlerRegistered = false;
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
            ReconnectLogger.Warning("Client disconnected. lastSession=" + (lastSession != null) + " lobby=" + (lastSession?.LobbyId ?? 0) + " session=" + Short(lastSession?.SessionId));
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
            clientMessageHandlerRegistered = false;
            nextHelloAt = 0f;
            ReconnectLogger.Info("Client connection became active; reset hello attempts.");
        }

        wasClientActive = clientActive;
    }

    private void TrackHostSession()
    {
        bool serverActive = NetworkServer.active;
        if (wasServerActive && !serverActive)
        {
            ReconnectLogger.Warning("Server stopped. restoreRestart=" + restoreHostRestartInProgress + " hostSession=" + (hostSession != null) + " lobby=" + (hostSession?.LobbyId ?? 0));
            ResetHostRuntimeState("server-stopped", clearSession: !restoreHostRestartInProgress);
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
            ReconnectLogger.Info("Reconnect join window state captured. prevAllow=" + reconnectPreviousAllowConnection + " prevDenyInDungeon=" + reconnectPreviousAccessDenyInDungeon);
        }

        HorayNetworkAuthenticator.allowConnection = true;
        HorayNetworkAuthenticator.AccessDeny_InDungeon = false;
        float seconds = Mathf.Max(30, config.ReconnectJoinWindowSeconds);
        float until = Time.unscaledTime + seconds;
        if (until > reconnectConnectionOpenUntil)
        {
            reconnectConnectionOpenUntil = until;
            ReconnectLogger.Info("Opened reconnect join window for " + seconds + "s: " + reason + " allowConnection=" + HorayNetworkAuthenticator.allowConnection + " denyInDungeon=" + HorayNetworkAuthenticator.AccessDeny_InDungeon);
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
        ReconnectLogger.Info("Closed reconnect join window. allowConnection=" + HorayNetworkAuthenticator.allowConnection + " denyInDungeon=" + HorayNetworkAuthenticator.AccessDeny_InDungeon);
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
                ReconnectLogger.Info("Restored Steam lobby joinable=false after reconnect window. " + DescribeLobby(lobby));
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to restore Steam lobby joinable state: " + ex.Message);
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

    internal static bool ShouldRestoreGiveUpButtonForReconnectWindow()
    {
        ReconnectController controller = Instance;
        if (controller == null || !controller.reconnectJoinWindowOpen || !NetworkServer.active)
        {
            return false;
        }

        try
        {
            return DungeonManager.Instance != null &&
                DungeonManager.Instance.isRunStarted &&
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

            bool reconnectAttempt = ShouldAttemptReconnectHello();
            nextHelloAt = Time.unscaledTime + Mathf.Max(5, config.ClientHelloIntervalSeconds);
            autoHelloAttempts++;
            ReconnectLogger.Info("Auto " + (reconnectAttempt ? "reconnect" : "session") + " hello attempt " + autoHelloAttempts + "/" + Mathf.Max(1, config.MaxAutoHelloAttempts) + ".");
            SendHello(reconnectAttempt);
        }
    }

    private bool ShouldAttemptReconnectHello()
    {
        if (clientHelloAccepted)
        {
            return false;
        }

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
        string runIdentity = GetRunIdentity();

        bool changedSave = hostSession != null && hostSession.SaveSlotId != saveSlot;
        bool changedRunIdentity = hostSession != null && ExtractRunIdentity(hostSession.RunId) != runIdentity;
        bool changedSaveOrRun = changedSave || changedRunIdentity;
        bool changedHost = hostSession != null
            && hostSession.HostSteamId != 0
            && lobby.LocalSteamId != 0
            && hostSession.HostSteamId != lobby.LocalSteamId;
        bool needsNewSession = hostSession == null
            || changedSaveOrRun
            || changedHost;

        if (needsNewSession)
        {
            Dictionary<ulong, ReconnectMemberRecord> carriedMembers = null;
            if (changedRunIdentity && !changedSave && !changedHost)
            {
                carriedMembers = CaptureMembersForSessionCarryover();
            }

            if (changedSaveOrRun)
            {
                ReconnectLogger.Info("Save slot or run changed; deleting ended session checkpoints. oldSave=" + NullToDash(hostSession?.SaveSlotId) + " oldRun=" + NullToDash(hostSession?.RunId) + " newSave=" + saveSlot + " newRun=" + runId);
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
            ApplyCarriedMembersToNewSession(carriedMembers);
            statusLine = "已创建房主重连会话。";
            ReconnectLogger.Info("Created host reconnect session. host=" + hostSession.HostSteamId + " lobby=" + hostSession.LobbyId + " session=" + Short(hostSession.SessionId) + " save=" + hostSession.SaveSlotId + " run=" + hostSession.RunId + " changedHost=" + changedHost);
            SaveHostSession();
        }
        else
        {
            if (hostSession.HostSteamId == 0 && lobby.LocalSteamId != 0)
            {
                hostSession.HostSteamId = lobby.LocalSteamId;
            }

            if (hostSession.RunId != runId)
            {
                ReconnectLogger.Info("Run progress changed; preserving reconnect session. oldRun=" + NullToDash(hostSession.RunId) + " newRun=" + runId + " session=" + Short(hostSession.SessionId));
                hostSession.RunId = runId;
            }

            hostSession.LobbyId = lobby.LobbyId;
            hostSession.UpdatedUtc = DateTime.UtcNow;
            hostSession.Members ??= new Dictionary<ulong, ReconnectMemberRecord>();
            hostSession.Checkpoints ??= new List<ReconnectCheckpointRecord>();
        }

        RememberLobbyMaxMembers(lobby, "ensure-host-session");
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
                ReconnectLogger.Info("Deleting session checkpoint. " + DescribeCheckpoint(checkpoint));
                store.DeleteCheckpointFiles(checkpoint);
            }
        }

        foreach (string checkpointId in store.EnumerateCheckpointIds().Distinct().ToList())
        {
            ReconnectLogger.Info("Deleting orphan/ended checkpoint files. id=" + checkpointId);
            store.DeleteCheckpointFiles(checkpointId);
        }
    }

    private Dictionary<ulong, ReconnectMemberRecord> CaptureMembersForSessionCarryover()
    {
        if (hostSession?.Members == null || hostSession.Members.Count == 0)
        {
            return null;
        }

        Dictionary<ulong, ReconnectMemberRecord> carried = new Dictionary<ulong, ReconnectMemberRecord>();
        foreach (KeyValuePair<ulong, ReconnectMemberRecord> pair in hostSession.Members)
        {
            ReconnectMemberRecord source = pair.Value;
            if (source == null || source.SteamId == 0 || source.State == ReconnectMemberState.Removed)
            {
                continue;
            }

            carried[pair.Key] = CloneMemberForSessionCarryover(source);
        }

        return carried.Count > 0 ? carried : null;
    }

    private void ApplyCarriedMembersToNewSession(Dictionary<ulong, ReconnectMemberRecord> carriedMembers)
    {
        if (hostSession == null || carriedMembers == null || carriedMembers.Count == 0)
        {
            return;
        }

        hostSession.Members ??= new Dictionary<ulong, ReconnectMemberRecord>();
        foreach (ReconnectMemberRecord member in carriedMembers.Values)
        {
            if (member == null || member.SteamId == 0)
            {
                continue;
            }

            member.SessionId = hostSession.SessionId;
            member.SaveSlotId = hostSession.SaveSlotId;
            member.RunId = hostSession.RunId;
            member.CheckpointId = hostSession.CurrentCheckpointId;
            if (member.State == ReconnectMemberState.ReconnectedWaiting)
            {
                member.State = ReconnectMemberState.Online;
            }

            hostSession.Members[member.SteamId] = member;
        }

        ReconnectLogger.Info("Carried members into new host session. count=" + hostSession.Members.Count + " session=" + Short(hostSession.SessionId));
    }

    private static ReconnectMemberRecord CloneMemberForSessionCarryover(ReconnectMemberRecord source)
    {
        return new ReconnectMemberRecord
        {
            SteamId = source.SteamId,
            PlayerName = source.PlayerName,
            SteamName = source.SteamName,
            PlayerSlot = source.PlayerSlot,
            ReconnectToken = string.IsNullOrEmpty(source.ReconnectToken) ? Guid.NewGuid().ToString("N") : source.ReconnectToken,
            SessionId = source.SessionId,
            SaveSlotId = source.SaveSlotId,
            RunId = source.RunId,
            CurrentFloorId = source.CurrentFloorId,
            CheckpointId = source.CheckpointId,
            ModInstalled = source.ModInstalled,
            LastHelloUtc = source.LastHelloUtc,
            LastSeenUtc = source.LastSeenUtc,
            State = source.State
        };
    }

    private void RefreshHostMembersFromPlayerSpawners()
    {
        if (hostSession == null || PlayerSpawner.MultiplayerList == null)
        {
            return;
        }

        LobbyInfo lobby = GetLobbyInfo();
        foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
        {
            if (spawner == null || spawner.steamID == 0)
            {
                continue;
            }

            ReconnectMemberRecord member = GetOrCreateMember(spawner.steamID);
            RememberConnectionSteamId(spawner);
            ReconnectMemberState previousState = member.State;
            int previousSlot = member.PlayerSlot;
            bool previousModInstalled = member.ModInstalled;
            UpdateMemberName(member, SafePlayerName(spawner));
            if (member.PlayerSlot < 0)
            {
                member.PlayerSlot = spawner.currentPlayerIdxForSave >= 0 ? spawner.currentPlayerIdxForSave : spawner.currentPlayerIdx;
            }
            if (lobby.LocalSteamId != 0 && spawner.steamID == lobby.LocalSteamId)
            {
                member.ModInstalled = true;
                if (member.LastHelloUtc == default)
                {
                    member.LastHelloUtc = DateTime.UtcNow;
                }
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

            if (previousState != member.State || previousSlot != member.PlayerSlot || previousModInstalled != member.ModInstalled)
            {
                ReconnectLogger.Info("Player spawner refreshed member. oldState=" + previousState + " oldSlot=" + previousSlot + " " + DescribeMember(member));
            }
        }
    }

    private void ResetHostRuntimeState(string reason, bool clearSession)
    {
        lastObservedFloorGuid = "";
        lastPublishedLobbyState = "";
        lastSavedHostSessionState = "";
        connectionSteamIds.Clear();
        serverMessageHandlerRegistered = false;
        nextHostSessionRefreshAt = 0f;
        nextHostPlayerRefreshAt = 0f;
        nextHostLobbyRefreshAt = 0f;
        nextHostLobbyPublishAt = 0f;
        nextSaveHostSessionAt = 0f;
        nextPanelMemberRefreshAt = 0f;
        nextPanelLobbyMemberRefreshAt = 0f;
        nextPanelTextRefreshAt = 0f;

        if (!clearSession)
        {
            ReconnectLogger.Info("Preserved host reconnect session during runtime reset. reason=" + reason + " session=" + Short(hostSession?.SessionId));
            return;
        }

        if (hostSession != null)
        {
            ReconnectLogger.Info("Clearing ended host reconnect session. reason=" + reason + " session=" + Short(hostSession.SessionId) + " checkpoints=" + (hostSession.Checkpoints?.Count ?? 0) + " members=" + (hostSession.Members?.Count ?? 0));
            DeleteEndedSessionCheckpoints();
        }

        hostSession = null;
        pendingLobbySettings = null;
        statusLine = "已离开上一局，等待新会话。";

        try
        {
            store?.DeleteHostSession();
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to delete ended host session file: " + ex.Message);
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
            LobbyData lobbyData = lobby.Manager.Lobby;
            RememberLobbyMaxMembers(lobbyData.MaxMembers, "refresh-lobby-members");
            LobbyMemberData[] members = lobbyData.Members;
            for (int i = 0; i < members.Length; i++)
            {
                UserData user = members[i].user;
                ulong steamId = user.SteamId;
                if (steamId == 0)
                {
                    continue;
                }

                bool knownBefore = hostSession.Members.ContainsKey(steamId);
                ReconnectMemberRecord member = GetOrCreateMember(steamId);
                UpdateMemberSteamName(member, user.Nickname);

                member.SaveSlotId = hostSession.SaveSlotId;
                member.RunId = hostSession.RunId;
                member.SessionId = hostSession.SessionId;
                member.LastSeenUtc = DateTime.UtcNow;
                if (!knownBefore)
                {
                    ReconnectLogger.Info("New lobby member observed. steam=" + steamId + " nickname=" + NullToDash(user.Nickname) + " lobby=" + lobby.LobbyId);
                }
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to refresh lobby members: " + ex.Message);
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
                ReconnectLogger.Info("Correcting player save slot for SteamID " + spawner.steamID + " from " + spawner.currentPlayerIdxForSave + " to " + member.PlayerSlot);
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
            RememberLobbyMaxMembers(lobbyData.MaxMembers, "publish-lobby-data");
            lobbyData["ReconnectMod"] = ProtocolVersion;
            lobbyData["ReconnectSessionId"] = hostSession.SessionId;
            lobbyData["ReconnectCheckpointId"] = hostSession.CurrentCheckpointId ?? "";
            lobbyData["ReconnectCheckpointHash"] = hostSession.CurrentCheckpointHash ?? "";
            lastPublishedLobbyState = state;
            ReconnectLogger.Info("Published Steam lobby reconnect data. " + DescribeLobby(lobby) + " session=" + Short(hostSession.SessionId) + " checkpoint=" + Short(hostSession.CurrentCheckpointId) + " hash=" + Short(hostSession.CurrentCheckpointHash) + " members=" + (hostSession.Members?.Count ?? 0));
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to publish lobby data: " + ex.Message);
        }
    }

    public static void NotifyFloorEntryCheckpointCandidate(bool allowSave, string floorGuid, bool runStarted)
    {
        Instance?.HandleFloorEntryCheckpointCandidate(allowSave, floorGuid, runStarted);
    }

    public static void NotifySteamLobbyEntered(LobbyData lobby, string source)
    {
        try
        {
            if (!IsReconnectLobby(lobby))
            {
                return;
            }

            if (SteamInvitation.waitForExternalConnect)
            {
                ReconnectLogger.Info("Reconnect lobby notification ignored because external connection is already pending. source=" + source + " lobby=" + lobby.SteamId.m_SteamID);
                return;
            }

            if (NetworkServer.active && lobby.IsOwner)
            {
                ReconnectLogger.Info("Reconnect lobby notification ignored by owner/host. source=" + source + " lobby=" + lobby.SteamId.m_SteamID);
                return;
            }

            if (!string.IsNullOrEmpty(lobby.GameVersion) && lobby.GameVersion != Application.version)
            {
                ReconnectLogger.Warning("Reconnect lobby notification ignored because game version differs. source=" + source + " lobby=" + lobby.SteamId.m_SteamID + " lobbyVersion=" + lobby.GameVersion + " localVersion=" + Application.version);
                return;
            }

            if (!HasLoadedSaveForReconnectJoin())
            {
                ReconnectLogger.Info("Reconnect lobby notification deferred until a save is loaded. source=" + source + " lobby=" + lobby.SteamId.m_SteamID + " selectedProfile=" + GetSelectedProfileForLog());
                return;
            }

            string address = ResolveReconnectLobbyHostAddress(lobby);
            if (string.IsNullOrEmpty(address))
            {
                ReconnectLogger.Warning("Reconnect lobby notification ignored because no connectable host address was found. source=" + source + " lobby=" + lobby.SteamId.m_SteamID + " owner=" + lobby.Owner.user.id);
                return;
            }

            ulong lobbyId = lobby.SteamId.m_SteamID;
            if (IsRecentReconnectLobbyConnectAttempt(lobbyId))
            {
                ReconnectLogger.Info("Reconnect lobby notification ignored because a connect attempt was just started. source=" + source + " lobby=" + lobbyId);
                return;
            }

            MarkReconnectLobbyConnectAttempt(lobbyId);
            ReconnectLogger.Info("Reconnect lobby notification accepted; connecting to host. source=" + source + " lobby=" + lobbyId + " address=" + address + " session=" + Short(ReadReconnectLobbyData(lobby, "ReconnectSessionId")) + " checkpoint=" + Short(ReadReconnectLobbyData(lobby, "ReconnectCheckpointId")));
            SteamInvitation.ConnectToOtherHost(address);
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to process reconnect lobby notification: " + ex.Message);
        }
    }

    private static bool HasLoadedSaveForReconnectJoin()
    {
        return DungeonManager.Instance != null &&
            SaveManager.Current != null &&
            !string.IsNullOrEmpty(SaveManager.Binded);
    }

    private static string GetSelectedProfileForLog()
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

        return SaveManager.Binded ?? "";
    }

    private static bool IsReconnectLobby(LobbyData lobby)
    {
        try
        {
            if (!lobby.IsValid)
            {
                return false;
            }

            string protocol = ReadReconnectLobbyData(lobby, "ReconnectMod");
            string sessionId = ReadReconnectLobbyData(lobby, "ReconnectSessionId");
            return protocol == ProtocolVersion || !string.IsNullOrEmpty(sessionId);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveReconnectLobbyHostAddress(LobbyData lobby)
    {
        try
        {
            LobbyGameServer gameServer = lobby.GameServer;
            if (gameServer.id.IsValid())
            {
                return gameServer.id.ToString();
            }
        }
        catch
        {
        }

        try
        {
            string owner = lobby.Owner.user.id.ToString();
            return string.IsNullOrEmpty(owner) || owner == "0" ? "" : owner;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsRecentReconnectLobbyConnectAttempt(ulong lobbyId)
    {
        return lobbyId != 0 &&
            lobbyId == lastReconnectLobbyConnectAttemptId &&
            Time.unscaledTime - lastReconnectLobbyConnectAttemptAt < 10f;
    }

    private static void MarkReconnectLobbyConnectAttempt(ulong lobbyId)
    {
        lastReconnectLobbyConnectAttemptId = lobbyId;
        lastReconnectLobbyConnectAttemptAt = Time.unscaledTime;
    }

    private static string ReadReconnectLobbyData(LobbyData lobby, string key)
    {
        try
        {
            return lobby[key] ?? "";
        }
        catch
        {
            return "";
        }
    }

    private void HandleFloorEntryCheckpointCandidate(bool allowSave, string floorGuid, bool runStarted)
    {
        if (!config.AutoCaptureFloorCheckpoint || !allowSave || !runStarted)
        {
            ReconnectLogger.Info("Floor checkpoint candidate skipped by flags. allowSave=" + allowSave + " runStarted=" + runStarted + " auto=" + config.AutoCaptureFloorCheckpoint + " floor=" + Short(floorGuid));
            return;
        }

        if (!NetworkServer.active)
        {
            ReconnectLogger.Info("Floor checkpoint candidate skipped because server is inactive. floor=" + Short(floorGuid));
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
            ReconnectLogger.Info("Floor checkpoint candidate skipped because local avatar is not ready. floor=" + Short(floorGuid) + " loading=" + (local?.PlayerAvatar?.loadingScreenType ?? -999));
            return;
        }

        if (string.IsNullOrEmpty(floorGuid) || floorGuid == lastObservedFloorGuid)
        {
            ReconnectLogger.Info("Floor checkpoint candidate skipped because floor is empty or already observed. floor=" + Short(floorGuid) + " last=" + Short(lastObservedFloorGuid));
            return;
        }

        if (!string.Equals(local.PlayerAvatar.currentFloorGuid, floorGuid, StringComparison.Ordinal))
        {
            ReconnectLogger.Info("Floor checkpoint candidate skipped because avatar floor differs. candidate=" + Short(floorGuid) + " avatar=" + Short(local.PlayerAvatar.currentFloorGuid));
            return;
        }

        if (local.PlayerAvatar.isInDungeon <= 0)
        {
            ReconnectLogger.Info("Floor checkpoint candidate skipped because avatar is not in dungeon. floor=" + Short(floorGuid) + " isInDungeon=" + local.PlayerAvatar.isInDungeon);
            return;
        }

        if (!ShouldCaptureFloorEntryCheckpoint(local.PlayerAvatar, floorGuid))
        {
            ReconnectLogger.Info("Floor checkpoint candidate rejected by dungeon validation. floor=" + Short(floorGuid));
            return;
        }

        lastObservedFloorGuid = floorGuid;
        EnsureLobbyChapterCachedForRun("first-floor-entry");
        ReconnectLogger.Info("Floor checkpoint candidate accepted. floor=" + Short(floorGuid));
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
            ReconnectLogger.Info("Capturing checkpoint. reason=" + reason + " activate=" + activateForRestore + " floor=" + Short(floorGuid) + " session=" + Short(hostSession.SessionId));
            FlushCurrentRunForCheckpoint(floorGuid);
            string lobbyChapter = ResolveLobbyChapterForCheckpoint();

            if (PlayerSpawner.MultiplayerList != null)
            {
                foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
                {
                    spawner?.SaveCurrentSessionData();
                }
            }

            FloorData floor = FindFloor(floorGuid);
            CaptureGenerationSnapshot(out int generationPlayerCount, out int generationMaxLuck);
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
                LobbyChapter = lobbyChapter,
                LobbyChapterRunIdentity = ExtractRunIdentity(hostSession.RunId),
                GenerationPlayerCount = generationPlayerCount,
                GenerationMaxLuck = generationMaxLuck,
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
            ReconnectLogger.Info("Checkpoint captured. " + DescribeCheckpoint(checkpoint) + " path=" + checkpoint.SnapshotPath);
            ShowSystemMessage(activateForRestore ? "已保存楼层入口重连点。" : "已保存非恢复检查点。");
        }
        catch (Exception ex)
        {
            statusLine = "保存检查点失败：" + ex.Message;
            ReconnectLogger.Error("Checkpoint failed: " + ex);
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
                ReconnectLogger.Info("Flushed DungeonManager session data before checkpoint. floor=" + Short(floorGuid));
            }
            catch (Exception ex)
            {
                ReconnectLogger.Warning("Failed to flush DungeonManager session data before checkpoint: " + ex.Message);
            }
        }

        SaveManager.CurrentRun.SetBool("RunStarted", value: true);
        SaveManager.CurrentRun.SetString("LastFloorGuid", floorGuid);
        UpsertDungeonEnvironmentValue("IsInDungeon", 1);
        ReconnectLogger.Info("Forced checkpoint run markers. floor=" + Short(floorGuid) + " runStarted=true IsInDungeon=1");
    }

    private static void CaptureGenerationSnapshot(out int playerCount, out int maxLuck)
    {
        playerCount = 0;
        maxLuck = 0;

        try
        {
            if (PlayerSpawner.MultiplayerList == null)
            {
                return;
            }

            playerCount = PlayerSpawner.MultiplayerList.Count;
            foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
            {
                if (spawner == null || spawner.PlayerAvatar == null)
                {
                    continue;
                }

                maxLuck = Mathf.Max(maxLuck, spawner.PlayerAvatar.GetCustomStat(ECustomStat.Luck));
            }

            ReconnectLogger.Info("Captured floor generation player snapshot. players=" + playerCount + " maxLuck=" + maxLuck);
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to capture floor generation player snapshot: " + ex.Message);
        }
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
            ReconnectLogger.Info("Pruned old checkpoint by count. " + DescribeCheckpoint(removed));
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
            ReconnectLogger.Info("Pruned orphan checkpoint files. id=" + checkpointId);
        }
    }

    private void PrepareRestoreCurrentCheckpoint()
    {
        if (!NetworkServer.active)
        {
            statusLine = "已跳过：只有房主/服务端可以准备恢复。";
            ReconnectLogger.Warning("Restore requested while server is inactive.");
            return;
        }

        if (hostSession == null || string.IsNullOrEmpty(hostSession.CurrentCheckpointId))
        {
            statusLine = "当前没有可恢复的检查点。";
            ReconnectLogger.Warning("Restore requested without current checkpoint. hostSession=" + (hostSession != null));
            return;
        }

        if (HasUnreturnedReservedMember())
        {
            statusLine = config.RequireAllPlayersBeforeRestore
                ? "还有掉线成员未回房；按配置仍继续执行本层恢复。"
                : "还有掉线成员未回房；将先恢复本层，之后可邀请成员回房。";
            ReconnectLogger.Warning("Restore requested with unreturned reserved members. requireAll=" + config.RequireAllPlayersBeforeRestore);
            ShowSystemMessage(statusLine);
        }

        ReconnectCheckpointRecord checkpoint = hostSession.Checkpoints.LastOrDefault(c => c.CheckpointId == hostSession.CurrentCheckpointId);
        if (checkpoint == null || !File.Exists(checkpoint.SnapshotPath))
        {
            statusLine = "检查点快照文件不存在。";
            ReconnectLogger.Warning("Restore checkpoint file missing. checkpoint=" + DescribeCheckpoint(checkpoint));
            return;
        }

        ReconnectLogger.Info("Restore requested. " + DescribeCheckpoint(checkpoint));
        if (!ValidateCheckpointSnapshot(checkpoint, out string invalidReason))
        {
            ReconnectLogger.Warning("Checkpoint validation failed before restore. reason=" + invalidReason + " " + DescribeCheckpoint(checkpoint));
            if (!TryRefreshCheckpointSnapshotFromCurrentRun(checkpoint) || !ValidateCheckpointSnapshot(checkpoint, out invalidReason))
            {
                statusLine = "检查点不可恢复：" + invalidReason;
                ReconnectLogger.Error("Restore aborted; checkpoint is still invalid. reason=" + invalidReason + " " + DescribeCheckpoint(checkpoint));
                ShowSystemMessage(statusLine);
                return;
            }

            ReconnectLogger.Info("Checkpoint validation passed after repair. " + DescribeCheckpoint(checkpoint));
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
            ReconnectLogger.Info("Checkpoint copied to TMP save. selectedProfile=" + selectedProfile + " target=" + target + " backup=" + (File.Exists(backup) ? backup : "-"));
            StartCoroutine(RestoreHostFromCheckpointCoroutine(selectedProfile, checkpoint.CheckpointId));
            statusLine = "正在执行本层恢复。";
            ShowSystemMessage("正在执行本层恢复。");
        }
        catch (Exception ex)
        {
            statusLine = "准备恢复失败：" + ex.Message;
            ReconnectLogger.Error("Prepare restore failed: " + ex);
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
        ReconnectLogger.Warning("Restore coroutine stopping host. selectedProfile=" + selectedProfile + " checkpoint=" + checkpointId + " serverPlayerIndex=" + HorayNetworkManager.serverPlayerIndex);
        pendingLobbySettings = CaptureCurrentLobbySettings();
        PrepareRuntimeStateForRestore();
        ResetPlayerEffectHud();
        restoreHostRestartInProgress = true;
        try
        {
            NetworkManager.singleton.StopHost();
        }
        catch (Exception ex)
        {
            restoreHostRestartInProgress = false;
            statusLine = "恢复失败：停止主机失败。";
            ReconnectLogger.Error("Restore failed while stopping host: " + ex);
            ShowSystemMessage(statusLine);
            yield break;
        }

        yield return null;
        yield return new WaitForSeconds(1f);
        ResetPlayerEffectHud();

        bool loadedProfile = SaveManager.Load(selectedProfile);
        bool loadedTmp = SaveManager.LoadTMP(selectedProfile);
        SaveManager.ApplyPostLoadSaveFixes();
        ReconnectLogger.Info("Restore save load results. profile=" + loadedProfile + " tmp=" + loadedTmp + " selectedProfile=" + selectedProfile);
        if (!loadedProfile || !loadedTmp)
        {
            ReconnectLogger.Error("Restore failed because save reload failed. profile=" + loadedProfile + " tmp=" + loadedTmp);
            AbortRestoreAfterHostStopped("restore-load-failed", "恢复失败：无法重新载入检查点存档。");
            yield break;
        }

        statusLine = "正在重启主机并回到检查点楼层……";
        BeginRestoreGenerationOverride(GetCurrentRestoreCheckpoint());
        try
        {
            NetworkManager.singleton.StartHost();
        }
        catch (Exception ex)
        {
            ReconnectLogger.Error("Restore failed while starting host: " + ex);
            AbortRestoreAfterHostStopped("restore-start-host-failed", "恢复失败：重启主机失败。");
            yield break;
        }

        float hostStartDeadline = Time.unscaledTime + 1f;
        while (!NetworkServer.active && Time.unscaledTime < hostStartDeadline)
        {
            yield return null;
        }

        if (!NetworkServer.active)
        {
            ReconnectLogger.Error("Restore failed because host did not become active after StartHost.");
            AbortRestoreAfterHostStopped("restore-start-host-inactive", "恢复失败：主机未能进入联机状态。");
            yield break;
        }

        restoreHostRestartInProgress = false;
        ReconnectLogger.Warning("Restore coroutine started host. checkpoint=" + checkpointId + " serverPlayerIndex=" + HorayNetworkManager.serverPlayerIndex);
        yield return new WaitForSeconds(1f);
        OpenReconnectJoinWindow("restore-host-started");
        EnsureHostSession();
        yield return EnsureSteamLobbyAndPublishAfterRestore();
        statusLine = "已请求恢复到检查点：" + checkpointId;
        ReconnectLogger.Info("Restore coroutine completed. checkpoint=" + checkpointId + " hostSession=" + (hostSession != null) + " lobby=" + (hostSession?.LobbyId ?? 0));
        ShowSystemMessage("已请求恢复到检查点楼层。");
    }

    private void AbortRestoreAfterHostStopped(string reason, string message)
    {
        restoreHostRestartInProgress = false;
        ClearRestoreGenerationOverride(reason);
        statusLine = message;
        if (!NetworkServer.active)
        {
            ResetHostRuntimeState(reason, clearSession: true);
            statusLine = message;
        }

        ShowSystemMessage(statusLine);
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
            checkpoint.LobbyChapter = ResolveLobbyChapterForCheckpoint();
            checkpoint.LobbyChapterRunIdentity = ExtractRunIdentity(hostSession?.RunId);
            CaptureGenerationSnapshot(out checkpoint.GenerationPlayerCount, out checkpoint.GenerationMaxLuck);
            if (hostSession != null && hostSession.CurrentCheckpointId == checkpoint.CheckpointId)
            {
                hostSession.CurrentCheckpointHash = checkpoint.CheckpointHash;
            }

            store.SaveCheckpointMeta(checkpoint);
            SaveHostSession();
            ReconnectLogger.Warning("Repaired checkpoint snapshot before restore: " + DescribeCheckpoint(checkpoint));
            return true;
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to repair checkpoint snapshot before restore: " + ex.Message);
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
                ReconnectLogger.Warning("Checkpoint validation failed: cannot load snapshot. " + DescribeCheckpoint(checkpoint));
                return false;
            }

            if (!data.GetBool("RunStarted", fallback: false))
            {
                reason = "快照不是局内状态。";
                ReconnectLogger.Warning("Checkpoint validation failed: RunStarted=false. " + DescribeCheckpoint(checkpoint));
                return false;
            }

            string savedFloorGuid = data.GetString("LastFloorGuid", "");
            if (string.IsNullOrEmpty(savedFloorGuid))
            {
                reason = "快照缺少楼层记录。";
                ReconnectLogger.Warning("Checkpoint validation failed: missing LastFloorGuid. " + DescribeCheckpoint(checkpoint));
                return false;
            }

            if (!string.IsNullOrEmpty(checkpoint.FloorGuid) && !string.Equals(savedFloorGuid, checkpoint.FloorGuid, StringComparison.Ordinal))
            {
                reason = "快照楼层和检查点记录不一致。";
                ReconnectLogger.Warning("Checkpoint validation failed: LastFloorGuid mismatch. saved=" + Short(savedFloorGuid) + " expected=" + Short(checkpoint.FloorGuid) + " " + DescribeCheckpoint(checkpoint));
                return false;
            }

            if (!SnapshotHasDungeonEnvironmentValue(data, "IsInDungeon", 1))
            {
                reason = "快照缺少地牢状态。";
                ReconnectLogger.Warning("Checkpoint validation failed: missing IsInDungeon=1. " + DescribeCheckpoint(checkpoint));
                return false;
            }

            ReconnectLogger.Info("Checkpoint validation passed. savedFloor=" + Short(savedFloorGuid) + " " + DescribeCheckpoint(checkpoint));
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            ReconnectLogger.Warning("Checkpoint validation threw exception: " + ex.Message + " " + DescribeCheckpoint(checkpoint));
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
            ReconnectLogger.Info("Ensuring Steam lobby after restore. " + DescribeLobby(lobby));
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
                ReconnectLogger.Warning("No active Steam lobby after restore; creating a new private lobby.");
                lobby.Manager.Create();
                OpenReconnectJoinWindow("create-lobby-after-restore");
                statusLine = "已恢复本层，正在重新创建 Steam 房间。";
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to ensure Steam lobby after restore: " + ex.Message);
            yield break;
        }

        float deadline = Time.unscaledTime + 8f;
        while (Time.unscaledTime < deadline)
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (lobby.HasLobby && lobby.LobbyId != 0)
            {
                ReconnectLogger.Info("Steam lobby ready after restore. " + DescribeLobby(lobby));
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
        ReconnectLogger.Warning("Timed out waiting for Steam lobby after restore.");
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
            string chapter = data["Chapter"];
            RememberLobbyChapter(chapter, "capture-lobby-settings");
            RememberLobbyMaxMembers(data.MaxMembers, "capture-lobby-settings");
            LobbySettingsSnapshot snapshot = new LobbySettingsSnapshot
            {
                Name = data.Name,
                GameVersion = data.GameVersion,
                Type = data.Type,
                MaxMembers = data.MaxMembers,
                Chapter = chapter
            };
            ReconnectLogger.Info("Captured Steam lobby settings. " + DescribeLobby(lobby) + " name=" + NullToDash(snapshot.Name) + " version=" + NullToDash(snapshot.GameVersion) + " type=" + snapshot.Type + " max=" + snapshot.MaxMembers + " chapter=" + NullToDash(snapshot.Chapter));
            return snapshot;
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to capture Steam lobby settings before restore: " + ex.Message);
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
            int targetMaxMembers = ResolveLobbyMaxMembersForRestore(pendingLobbySettings, out string maxMembersSource);
            if (config == null || config.ForceLobbyJoinableForReconnect)
            {
                lobby.Manager.SetJoinable(makeJoinable: true);
            }

            if (pendingLobbySettings == null)
            {
                string chapter = ResolveLobbyChapterForRestore("");
                data.Type = ELobbyType.k_ELobbyTypePrivate;
                data.MaxMembers = targetMaxMembers;
                data.GameVersion = Application.version;
                data.Name = BuildFallbackLobbyName();
                data["Chapter"] = chapter;
                data.SetGameServer(UserData.Me.id);
                RememberLobbyChapter(chapter, "apply-fallback-lobby-settings");
                RememberLobbyMaxMembers(data.MaxMembers, "apply-fallback-lobby-settings");
                ReconnectLogger.Info("Applied fallback Steam lobby settings. " + DescribeLobby(lobby) + " name=" + NullToDash(data.Name) + " version=" + NullToDash(data.GameVersion) + " type=" + data.Type + " max=" + data.MaxMembers + " maxSource=" + maxMembersSource + " chapter=" + NullToDash(data["Chapter"]));
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
            data.MaxMembers = targetMaxMembers;

            string restoredChapter = ResolveLobbyChapterForRestore(pendingLobbySettings.Chapter);
            data["Chapter"] = restoredChapter;

            data.SetGameServer(UserData.Me.id);
            RememberLobbyChapter(restoredChapter, "apply-restored-lobby-settings");
            RememberLobbyMaxMembers(data.MaxMembers, "apply-restored-lobby-settings");
            ReconnectLogger.Info("Restored Steam lobby settings. " + DescribeLobby(lobby) + " name=" + NullToDash(data.Name) + " version=" + NullToDash(data.GameVersion) + " type=" + data.Type + " max=" + data.MaxMembers + " maxSource=" + maxMembersSource + " chapter=" + NullToDash(data["Chapter"]));
            pendingLobbySettings = null;
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to restore Steam lobby settings after restore: " + ex.Message);
        }
    }

    private static string BuildFallbackLobbyName()
    {
        string playerName = SafeLocalPlayerName();
        return string.IsNullOrEmpty(playerName) ? "Sephiria Reconnect" : playerName + "的房间";
    }

    private string ResolveLobbyChapterForCheckpoint()
    {
        string cachedChapter = GetCachedLobbyChapter();
        if (!string.IsNullOrEmpty(cachedChapter))
        {
            return cachedChapter;
        }

        try
        {
            LobbyInfo lobby = GetLobbyInfo();
            if (TryReadLobbyChapter(lobby, out string lobbyChapter))
            {
                RememberLobbyChapter(lobbyChapter, "checkpoint-lobby");
                return lobbyChapter;
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to read Steam lobby chapter for checkpoint: " + ex.Message);
        }

        string builtChapter = BuildLobbyChapterValue();
        if (IsUsefulBuiltLobbyChapterValue(builtChapter))
        {
            RememberLobbyChapter(builtChapter, "checkpoint-derived");
            return builtChapter;
        }

        return string.IsNullOrEmpty(cachedChapter) ? builtChapter : cachedChapter;
    }

    private string ResolveLobbyChapterForRestore(string preferredChapter)
    {
        string cachedChapter = GetCachedLobbyChapter();
        if (!string.IsNullOrEmpty(cachedChapter))
        {
            return cachedChapter;
        }

        preferredChapter = NormalizeLobbyChapterValue(preferredChapter);
        ReconnectCheckpointRecord checkpoint = GetCurrentRestoreCheckpoint();
        string expectedRunIdentity = ExtractRunIdentity(hostSession?.RunId);
        string checkpointChapter = NormalizeLobbyChapterValue(checkpoint == null ? "" : checkpoint.LobbyChapter);
        if (IsValidLobbyChapterValue(checkpointChapter) && IsLobbyChapterCacheForCurrentRun(checkpoint.LobbyChapterRunIdentity, expectedRunIdentity))
        {
            return checkpointChapter;
        }

        if (IsValidLobbyChapterValue(preferredChapter))
        {
            return preferredChapter;
        }

        return BuildLobbyChapterValue();
    }

    private ReconnectCheckpointRecord GetCurrentRestoreCheckpoint()
    {
        if (hostSession == null || hostSession.Checkpoints == null || string.IsNullOrEmpty(hostSession.CurrentCheckpointId))
        {
            return null;
        }

        return hostSession.Checkpoints.LastOrDefault(c => c != null && c.CheckpointId == hostSession.CurrentCheckpointId);
    }

    private static void BeginRestoreGenerationOverride(ReconnectCheckpointRecord checkpoint)
    {
        ClearRestoreGenerationOverride("begin-new-restore");
        if (checkpoint == null || checkpoint.GenerationPlayerCount <= 0 || string.IsNullOrEmpty(checkpoint.FloorGuid))
        {
            ReconnectLogger.Info("Restore generation snapshot unavailable; vanilla PlayerSpawner state will be used for floor generation.");
            return;
        }

        restoreGenerationFloorGuid = checkpoint.FloorGuid;
        restoreGenerationPlayerCount = Mathf.Max(1, checkpoint.GenerationPlayerCount);
        restoreGenerationMaxLuck = Mathf.Max(0, checkpoint.GenerationMaxLuck);
        restoreGenerationUntil = Time.unscaledTime + 45f;
        ReconnectLogger.Info("Enabled checkpoint floor generation snapshot. floor=" + Short(restoreGenerationFloorGuid) +
            " players=" + restoreGenerationPlayerCount +
            " maxLuck=" + restoreGenerationMaxLuck +
            " checkpoint=" + Short(checkpoint.CheckpointId));
    }

    private static void TickRestoreGenerationOverride()
    {
        if (!HasRestoreGenerationOverride())
        {
            return;
        }

        try
        {
            FloorGenerator floor = FloorGenerator.FindByGuid(restoreGenerationFloorGuid);
            if (floor != null && floor.GenerateSuccess)
            {
                ClearRestoreGenerationOverride("floor-generated");
            }
        }
        catch
        {
        }
    }

    private static bool HasRestoreGenerationOverride()
    {
        if (restoreGenerationPlayerCount <= 0 || string.IsNullOrEmpty(restoreGenerationFloorGuid))
        {
            return false;
        }

        if (restoreGenerationUntil > 0f && Time.unscaledTime > restoreGenerationUntil)
        {
            ClearRestoreGenerationOverride("expired");
            return false;
        }

        return true;
    }

    private static void ClearRestoreGenerationOverride(string reason)
    {
        if (restoreGenerationPlayerCount > 0 || !string.IsNullOrEmpty(restoreGenerationFloorGuid))
        {
            ReconnectLogger.Info("Cleared checkpoint floor generation snapshot. reason=" + reason +
                " floor=" + Short(restoreGenerationFloorGuid) +
                " players=" + restoreGenerationPlayerCount +
                " maxLuck=" + restoreGenerationMaxLuck);
        }

        restoreGenerationFloorGuid = "";
        restoreGenerationPlayerCount = 0;
        restoreGenerationMaxLuck = 0;
        restoreGenerationUntil = 0f;
    }

    internal static void ApplyRestoreGenerationSnapshotToAllocatedFloor(string floorGuid)
    {
        if (!HasRestoreGenerationOverride() || string.IsNullOrEmpty(floorGuid) || !string.Equals(floorGuid, restoreGenerationFloorGuid, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            FloorGenerator floor = FloorGenerator.FindByGuid(floorGuid);
            if (floor == null)
            {
                return;
            }

            int luckyType = ComputeLuckyType(floor.seed, restoreGenerationMaxLuck);
            if (floor.luckyType != luckyType)
            {
                floor.NetworkluckyType = luckyType;
                ReconnectLogger.Info("Applied checkpoint Lucky snapshot to restored floor. floor=" + Short(floorGuid) +
                    " seed=" + floor.seed +
                    " maxLuck=" + restoreGenerationMaxLuck +
                    " luckyType=" + luckyType);
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to apply checkpoint Lucky snapshot to restored floor: " + ex.Message);
        }
    }

    internal static void OverrideMonsterPhasePlayerCount(ref int multiplayerCount)
    {
        if (!HasRestoreGenerationOverride())
        {
            return;
        }

        multiplayerCount = restoreGenerationPlayerCount;
    }

    internal static void OverrideMonsterSpawnDifficulty(ref int difficulty)
    {
        if (!HasRestoreGenerationOverride())
        {
            return;
        }

        int actualCount = PlayerSpawner.MultiplayerList == null ? 0 : PlayerSpawner.MultiplayerList.Count;
        int delta = GetMultiplayerDifficultyBonus(restoreGenerationPlayerCount) - GetMultiplayerDifficultyBonus(actualCount);
        if (delta != 0)
        {
            difficulty += delta;
        }
    }

    private static int ComputeLuckyType(int seed, int maxLuck)
    {
        int luck = Mathf.Min(Mathf.Max(0, maxLuck), 20);
        double threshold = 4.0 + (double)luck * 0.05;
        return new System.Random(seed).NextDouble() * 100.0 < threshold ? 1 : 0;
    }

    private static int GetMultiplayerDifficultyBonus(int playerCount)
    {
        if (playerCount > 3)
        {
            return 2;
        }

        return playerCount > 1 ? 1 : 0;
    }

    private string GetCachedLobbyChapter()
    {
        string expectedRunIdentity = ExtractRunIdentity(hostSession?.RunId);
        string sessionChapter = NormalizeLobbyChapterValue(hostSession == null ? "" : hostSession.LastKnownLobbyChapter);
        if (IsValidLobbyChapterValue(sessionChapter) && IsLobbyChapterCacheForCurrentRun(hostSession.LastKnownLobbyChapterRunIdentity, expectedRunIdentity))
        {
            return sessionChapter;
        }

        ReconnectCheckpointRecord checkpoint = GetCurrentRestoreCheckpoint();
        string checkpointChapter = NormalizeLobbyChapterValue(checkpoint == null ? "" : checkpoint.LobbyChapter);
        return IsValidLobbyChapterValue(checkpointChapter) && IsLobbyChapterCacheForCurrentRun(checkpoint.LobbyChapterRunIdentity, expectedRunIdentity) ? checkpointChapter : "";
    }

    private bool RememberLobbyChapter(string chapter, string source)
    {
        if (hostSession == null)
        {
            return false;
        }

        chapter = NormalizeLobbyChapterValue(chapter);
        if (!IsValidLobbyChapterValue(chapter))
        {
            return false;
        }

        string runIdentity = ExtractRunIdentity(hostSession.RunId);
        if (hostSession.LastKnownLobbyChapter == chapter && IsLobbyChapterCacheForCurrentRun(hostSession.LastKnownLobbyChapterRunIdentity, runIdentity))
        {
            return false;
        }

        string cachedChapter = GetCachedLobbyChapter();
        if (!string.IsNullOrEmpty(cachedChapter) && cachedChapter != chapter)
        {
            ReconnectLogger.Info("Keeping cached Steam lobby chapter. source=" + source + " cached=" + cachedChapter + " ignored=" + chapter + " session=" + Short(hostSession.SessionId));
            return false;
        }

        hostSession.LastKnownLobbyChapter = chapter;
        hostSession.LastKnownLobbyChapterRunIdentity = runIdentity;
        ReconnectLogger.Info("Cached Steam lobby chapter. source=" + source + " chapter=" + chapter + " session=" + Short(hostSession.SessionId));
        return true;
    }

    private bool RememberLobbyMaxMembers(LobbyInfo lobby, string source)
    {
        if (!lobby.HasLobby || lobby.Manager == null)
        {
            return false;
        }

        try
        {
            return RememberLobbyMaxMembers(lobby.Manager.Lobby.MaxMembers, source);
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to cache Steam lobby max members. source=" + source + " error=" + ex.Message);
            return false;
        }
    }

    private bool RememberLobbyMaxMembers(int maxMembers, string source)
    {
        if (hostSession == null || maxMembers <= 0)
        {
            return false;
        }

        if (hostSession.LastKnownLobbyMaxMembers == maxMembers)
        {
            return false;
        }

        hostSession.LastKnownLobbyMaxMembers = maxMembers;
        ReconnectLogger.Info("Cached Steam lobby max members. source=" + source + " raw=" + maxMembers + " applied=" + ClampLobbyMaxMembers(maxMembers) + " limit=" + GetMaxAllowedLobbyMembers() + " session=" + Short(hostSession.SessionId));
        SaveHostSession();
        return true;
    }

    private int ResolveLobbyMaxMembersForRestore(LobbySettingsSnapshot snapshot, out string source)
    {
        int inferred = InferLobbyMaxMembersFromSession();
        if (snapshot != null && snapshot.MaxMembers > 0)
        {
            source = "captured-lobby";
            return ClampLobbyMaxMembers(Mathf.Max(snapshot.MaxMembers, inferred));
        }

        if (hostSession != null && hostSession.LastKnownLobbyMaxMembers > 0)
        {
            source = "cached-session";
            return ClampLobbyMaxMembers(Mathf.Max(hostSession.LastKnownLobbyMaxMembers, inferred));
        }

        int fallback = GetFallbackLobbyMaxMembers();
        if (inferred > 0)
        {
            source = "inferred-session";
            return ClampLobbyMaxMembers(Mathf.Max(inferred, fallback));
        }

        source = "config-default";
        return fallback;
    }

    private int InferLobbyMaxMembersFromSession()
    {
        int candidate = 0;
        int highestSlot = -1;

        if (hostSession?.Members != null)
        {
            int knownMembers = 0;
            foreach (ReconnectMemberRecord member in hostSession.Members.Values)
            {
                if (member == null || member.SteamId == 0 || member.State == ReconnectMemberState.Removed)
                {
                    continue;
                }

                knownMembers++;
                if (member.PlayerSlot >= 0 && member.PlayerSlot > highestSlot)
                {
                    highestSlot = member.PlayerSlot;
                }
            }

            candidate = Mathf.Max(candidate, knownMembers);
        }

        if (highestSlot >= 0)
        {
            candidate = Mathf.Max(candidate, highestSlot + 1);
        }

        if (PlayerSpawner.MultiplayerList != null)
        {
            int activeSpawners = 0;
            foreach (PlayerSpawner spawner in PlayerSpawner.MultiplayerList)
            {
                if (spawner != null)
                {
                    activeSpawners++;
                }
            }

            candidate = Mathf.Max(candidate, activeSpawners);
        }

        return candidate > 0 ? ClampLobbyMaxMembers(candidate) : 0;
    }

    private int GetFallbackLobbyMaxMembers()
    {
        int fallback = config == null ? new ReconnectConfig().FallbackLobbyMaxMembers : config.FallbackLobbyMaxMembers;
        if (fallback <= 0)
        {
            fallback = new ReconnectConfig().FallbackLobbyMaxMembers;
        }

        return ClampLobbyMaxMembers(fallback);
    }

    private int GetMaxAllowedLobbyMembers()
    {
        int maxAllowed = config == null ? new ReconnectConfig().MaxAllowedLobbyMembers : config.MaxAllowedLobbyMembers;
        if (maxAllowed <= 0)
        {
            maxAllowed = new ReconnectConfig().MaxAllowedLobbyMembers;
        }

        return Mathf.Max(1, maxAllowed);
    }

    private int ClampLobbyMaxMembers(int maxMembers)
    {
        return Mathf.Clamp(maxMembers, 1, GetMaxAllowedLobbyMembers());
    }

    private bool EnsureLobbyChapterCachedForRun(string source)
    {
        if (!string.IsNullOrEmpty(GetCachedLobbyChapter()))
        {
            return false;
        }

        string chapter = ResolveLobbyChapterForCheckpoint();
        return RememberLobbyChapter(chapter, source);
    }

    private static bool TryReadLobbyChapter(LobbyInfo lobby, out string chapter)
    {
        chapter = "";
        try
        {
            if (!lobby.HasLobby || lobby.Manager == null)
            {
                return false;
            }

            chapter = NormalizeLobbyChapterValue(lobby.Manager.Lobby["Chapter"]);
            return IsValidLobbyChapterValue(chapter);
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to read Steam lobby chapter: " + ex.Message);
            chapter = "";
            return false;
        }
    }

    private static bool IsUsefulBuiltLobbyChapterValue(string value)
    {
        value = NormalizeLobbyChapterValue(value);
        return IsValidLobbyChapterValue(value) && value != "0";
    }

    private static bool IsLobbyChapterCacheForCurrentRun(string cachedRunIdentity, string expectedRunIdentity)
    {
        cachedRunIdentity = NormalizeLobbyChapterValue(cachedRunIdentity);
        expectedRunIdentity = NormalizeLobbyChapterValue(expectedRunIdentity);
        return !string.IsNullOrEmpty(cachedRunIdentity) && cachedRunIdentity == expectedRunIdentity;
    }

    private static string NormalizeLobbyChapterValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "" : value.Trim();
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
            ReconnectLogger.Warning("Failed to build Steam lobby chapter value: " + ex.Message);
        }

        return "0";
    }

    private static bool IsValidLobbyChapterValue(string value)
    {
        value = NormalizeLobbyChapterValue(value);
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
            ReconnectLogger.Warning("Failed to scan network connections before restore: " + ex.Message);
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
            ReconnectLogger.Warning("Failed to scan multiplayer list before restore: " + ex.Message);
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
            ReconnectLogger.Warning("Failed to clear global item stat table before restore: " + ex.Message);
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
            ReconnectLogger.Error("Error resetting player runtime before restore: " + ex);
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
            ReconnectLogger.Warning("Failed to reset player effect HUD: " + ex.Message);
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
            ReconnectLogger.Warning("Join last lobby requested without last session.");
            return;
        }

        try
        {
            ReconnectLogger.Info("Join last lobby requested. lobby=" + lastSession.LobbyId + " host=" + lastSession.HostSteamId + " session=" + Short(lastSession.SessionId) + " checkpoint=" + Short(lastSession.CheckpointId));
            GameObject steam = SingletonObject.Find("SteamManager");
            if (!steam || !steam.TryGetComponent<LobbyManager>(out var lobbyManager))
            {
                statusLine = "未找到 Steam 房间管理器。";
                ReconnectLogger.Warning("Join last lobby aborted; Steam LobbyManager not found.");
                return;
            }

            LobbyData lobby = LobbyData.Get(lastSession.LobbyId.ToString());
            StartCoroutine(JoinLastLobbyCoroutine(lobbyManager, lobby));
            statusLine = "正在返回上次房间：" + lastSession.LobbyId;
        }
        catch (Exception ex)
        {
            statusLine = "加入上次房间失败：" + ex.Message;
            ReconnectLogger.Error("Join last lobby failed: " + ex);
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
                ReconnectLogger.Info("Joined last Steam lobby; connecting to owner. lobby=" + lobby.SteamId.m_SteamID + " owner=" + lobby.Owner.user.id);
                SteamInvitation.ConnectToOtherHost(lobby.Owner.user.id.ToString());
                statusLine = "已回到上次房间，正在连接房主。";
                yield break;
            }

            yield return null;
        }

        statusLine = "加入上次房间超时。";
        ReconnectLogger.Warning("Join last lobby timed out. targetLobby=" + lobby.SteamId.m_SteamID);
        ShowSystemMessage(statusLine);
    }

    private void SendHello(bool reconnectAttempt)
    {
        if (!NetworkClient.active)
        {
            statusLine = "当前客户端未连接，无法发送握手。";
            ReconnectLogger.Warning("Hello send skipped because NetworkClient is inactive. reconnect=" + reconnectAttempt);
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
            ReconnectLogger.Info("Sending hello. " + DescribeHello(msg) + " lobbySession=" + Short(lobbySessionId) + " " + DescribeLobby(lobby));
            NetworkClient.Send(msg);
            statusLine = reconnectAttempt ? "已发送重连握手。" : "已发送联机会话握手。";
        }
        catch (Exception ex)
        {
            statusLine = "发送握手失败：" + ex.Message;
            ReconnectLogger.Error("Send hello failed: " + ex);
        }
    }

    private void HandleServerHello(NetworkConnectionToClient conn, ReconnectHelloMessage msg)
    {
        EnsureHostSession();
        ReconnectHelloReplyMessage reply = ValidateHello(conn, msg);
        ReconnectLogger.Info("Received hello. accepted=" + reply.accepted + " reason=" + NullToDash(reply.reason) + " " + DescribeHello(msg) + " conn=" + (conn != null ? conn.connectionId.ToString() : "-"));
        if (reply.accepted)
        {
            ReconnectMemberRecord member = GetOrCreateMember(msg.playerSteamId);
            ReconnectMemberState previousState = member.State;
            int previousSlot = member.PlayerSlot;
            UpdateMemberName(member, msg.playerName);
            member.ModInstalled = true;
            member.LastHelloUtc = DateTime.UtcNow;
            if (member.PlayerSlot < 0 && conn != null && conn.identity != null && conn.identity.TryGetComponent<PlayerSpawner>(out PlayerSpawner helloSpawner))
            {
                member.PlayerSlot = helloSpawner.currentPlayerIdxForSave >= 0 ? helloSpawner.currentPlayerIdxForSave : helloSpawner.currentPlayerIdx;
            }
            member.SaveSlotId = hostSession.SaveSlotId;
            member.RunId = hostSession.RunId;
            member.SessionId = hostSession.SessionId;
            member.LastSeenUtc = DateTime.UtcNow;
            member.CheckpointId = hostSession.CurrentCheckpointId;
            bool currentOnlineRefresh = msg.reconnectAttempt
                && previousState == ReconnectMemberState.Online
                && ConnectionMatchesSteamId(conn, msg.playerSteamId);
            member.State = msg.reconnectAttempt && !currentOnlineRefresh
                ? ReconnectMemberState.ReconnectedWaiting
                : ReconnectMemberState.Online;
            if (conn != null && conn.identity != null && conn.identity.TryGetComponent<PlayerSpawner>(out PlayerSpawner reconnectSpawner) && member.PlayerSlot >= 0)
            {
                reconnectSpawner.currentPlayerIdxForSave = member.PlayerSlot;
            }
            ReconnectLogger.Info("Hello accepted and member updated. oldState=" + previousState + " oldSlot=" + previousSlot + " " + DescribeMember(member));
            SaveHostSession();
        }

        try
        {
            conn?.Send(reply);
            ReconnectLogger.Info("Sent hello reply. accepted=" + reply.accepted + " reason=" + NullToDash(reply.reason) + " to=" + msg.playerSteamId);
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to reply hello: " + ex.Message);
        }
    }

    private ReconnectHelloReplyMessage ValidateHello(NetworkConnectionToClient conn, ReconnectHelloMessage msg)
    {
        ReconnectHelloReplyMessage reply = new ReconnectHelloReplyMessage
        {
            accepted = false,
            reason = "",
            hostSteamId = hostSession.HostSteamId,
            lobbyId = hostSession.LobbyId,
            saveSlotId = string.IsNullOrEmpty(msg.saveSlotId) ? hostSession.SaveSlotId : msg.saveSlotId,
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
            ReconnectLogger.Info("Hello save slot differs; treating save slot as local metadata. clientSlot=" + msg.saveSlotId + " hostSlot=" + hostSession.SaveSlotId);
        }

        if (!string.IsNullOrEmpty(msg.runId) && ExtractRunIdentity(msg.runId) != ExtractRunIdentity(hostSession.RunId))
        {
            ReconnectLogger.Info("Hello run identity differs; accepting if session/token checks pass. clientRun=" + msg.runId + " hostRun=" + hostSession.RunId + " reconnect=" + msg.reconnectAttempt);
        }

        bool knownMember = hostSession.Members.TryGetValue(msg.playerSteamId, out ReconnectMemberRecord member);
        if (!string.IsNullOrEmpty(msg.sessionId) && msg.sessionId != hostSession.SessionId)
        {
            bool allowCarriedReconnect = msg.reconnectAttempt
                && knownMember
                && !string.IsNullOrEmpty(msg.reconnectToken)
                && msg.reconnectToken == member.ReconnectToken;
            if (!allowCarriedReconnect)
            {
                reply.reason = "会话编号不匹配。";
                return reply;
            }

            ReconnectLogger.Info("Hello session differs but token matches carried member; accepting reconnect into current session. clientSession=" + Short(msg.sessionId) + " hostSession=" + Short(hostSession.SessionId) + " steam=" + msg.playerSteamId);
        }

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
                if (!ConnectionMatchesSteamId(conn, msg.playerSteamId))
                {
                    reply.reason = "该玩家仍被记录为在线，拒绝重复重连。";
                    return reply;
                }

                ReconnectLogger.Info("Reconnect hello came from the current online player; treating it as a session refresh. steam=" + msg.playerSteamId + " conn=" + (conn != null ? conn.connectionId.ToString() : "-"));
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
        ReconnectLogger.Info("Received hello reply. accepted=" + msg.accepted + " reason=" + NullToDash(msg.reason) + " host=" + msg.hostSteamId + " lobby=" + msg.lobbyId + " session=" + Short(msg.sessionId) + " checkpoint=" + Short(msg.checkpointId));
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
        ReconnectLogger.Info("Saved last session from hello reply. player=" + lastSession.PlayerSteamId + " host=" + lastSession.HostSteamId + " lobby=" + lastSession.LobbyId + " session=" + Short(lastSession.SessionId));
    }

    private void HandleServerDisconnect(NetworkConnectionToClient conn)
    {
        if (hostSession == null || conn == null)
        {
            ReconnectLogger.Warning("Server disconnect event ignored because session/connection is missing. hostSession=" + (hostSession != null) + " conn=" + (conn != null));
            return;
        }

        PlayerSpawner spawner = conn.identity != null ? conn.identity.GetComponent<PlayerSpawner>() : null;
        ulong steamId = spawner != null ? spawner.steamID : 0;
        if (steamId == 0 && connectionSteamIds.TryGetValue(conn.connectionId, out ulong rememberedSteamId))
        {
            steamId = rememberedSteamId;
        }

        if (steamId == 0)
        {
            ReconnectLogger.Warning("Server disconnect event ignored because SteamID is missing. conn=" + conn.connectionId + " identity=" + (conn.identity != null));
            return;
        }

        ReconnectMemberRecord member = GetOrCreateMember(steamId);
        ReconnectMemberState previousState = member.State;
        if (spawner != null)
        {
            UpdateMemberName(member, SafePlayerName(spawner));
            member.CurrentFloorId = SafeFloorGuid(spawner);
        }
        member.LastSeenUtc = DateTime.UtcNow;
        member.State = ReconnectMemberState.OfflineReserved;
        member.CheckpointId = hostSession.CurrentCheckpointId;
        SaveHostSession();
        connectionSteamIds.Remove(conn.connectionId);
        statusLine = "已保留掉线玩家：" + member.SteamId;
        ReconnectLogger.Warning("Server disconnect reserved member. oldState=" + previousState + " " + DescribeMember(member) + " conn=" + conn.connectionId);
    }

    private void RememberConnectionSteamId(PlayerSpawner spawner)
    {
        if (spawner == null || spawner.steamID == 0 || spawner.connectionToClient == null)
        {
            return;
        }

        connectionSteamIds[spawner.connectionToClient.connectionId] = spawner.steamID;
    }

    private bool ConnectionMatchesSteamId(NetworkConnectionToClient conn, ulong steamId)
    {
        if (conn == null || steamId == 0)
        {
            return false;
        }

        PlayerSpawner spawner = conn.identity != null ? conn.identity.GetComponent<PlayerSpawner>() : null;
        if (spawner != null && spawner.steamID == steamId)
        {
            return true;
        }

        return connectionSteamIds.TryGetValue(conn.connectionId, out ulong rememberedSteamId)
            && rememberedSteamId == steamId;
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
            ReconnectLogger.Info("Created member record. " + DescribeMember(member));
        }

        return member;
    }

    private static bool UpdateMemberName(ReconnectMemberRecord member, string name)
    {
        if (member == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (string.Equals(member.PlayerName, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        member.PlayerName = trimmed;
        return true;
    }

    private static bool UpdateMemberSteamName(ReconnectMemberRecord member, string name)
    {
        if (member == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (string.Equals(member.SteamName, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        member.SteamName = trimmed;
        return true;
    }

    public static bool TryGetReservedPlayerSlot(ulong steamId, out int slot)
    {
        slot = -1;
        ReconnectController controller = Instance;
        if (controller == null || controller.hostSession == null)
        {
            ReconnectLogger.Info("Reserved slot lookup failed because controller/session is missing. steam=" + steamId);
            return false;
        }

        if (!controller.hostSession.Members.TryGetValue(steamId, out ReconnectMemberRecord member))
        {
            ReconnectLogger.Info("Reserved slot lookup failed because member is unknown. steam=" + steamId);
            return false;
        }

        if (member.PlayerSlot < 0)
        {
            ReconnectLogger.Info("Reserved slot lookup failed because slot is not assigned. " + DescribeMember(member));
            return false;
        }

        if (!CanReuseReservedPlayerSlot(member.State))
        {
            ReconnectLogger.Warning("Reserved slot lookup refused because member is not in a reusable state. " + DescribeMember(member));
            return false;
        }

        slot = member.PlayerSlot;
        ReconnectLogger.Info("Reserved slot lookup succeeded. " + DescribeMember(member));
        return true;
    }

    private static bool CanReuseReservedPlayerSlot(ReconnectMemberState state)
    {
        return state == ReconnectMemberState.OfflineReserved ||
            state == ReconnectMemberState.ReconnectPending ||
            state == ReconnectMemberState.ReconnectedWaiting;
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
        lastSavedHostSessionState = signature;
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
            .Append(hostSession.LastKnownLobbyChapter).Append('|')
            .Append(hostSession.LastKnownLobbyChapterRunIdentity).Append('|')
            .Append(hostSession.LastKnownLobbyMaxMembers).Append('|')
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
                .Append(checkpoint.LobbyChapter).Append('|')
                .Append(checkpoint.LobbyChapterRunIdentity).Append('|')
                .Append(checkpoint.GenerationPlayerCount).Append('|')
                .Append(checkpoint.GenerationMaxLuck).Append('|')
                .Append(checkpoint.CreatedUtc.Ticks);
        }

        foreach (ReconnectMemberRecord member in hostSession.Members.Values.OrderBy(m => m.SteamId))
        {
            builder.Append('\n')
                .Append(member.SteamId).Append('|')
                .Append(member.PlayerName).Append('|')
                .Append(member.SteamName).Append('|')
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

    private string GetRunIdentity()
    {
        try
        {
            if (SaveManager.CurrentRun == null)
            {
                return "未开始";
            }

            int seed = SaveManager.CurrentRun.GetInt("Seed", 0);
            int game = SaveManager.CurrentRun.GetInt("CurrentGame", -1);
            return seed + ":" + game;
        }
        catch
        {
            return "未知";
        }
    }

    private static string ExtractRunIdentity(string runId)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return "";
        }

        string[] parts = runId.Split(':');
        return parts.Length >= 2 ? parts[0] + ":" + parts[1] : runId;
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

    private static string DescribeLobby(LobbyInfo lobby)
    {
        return "has=" + lobby.HasLobby +
            " lobby=" + lobby.LobbyId +
            " owner=" + lobby.IsOwner +
            " local=" + lobby.LocalSteamId +
            " host=" + lobby.HostSteamId;
    }

    private static string DescribeMember(ReconnectMemberRecord member)
    {
        if (member == null)
        {
            return "<null>";
        }

        return "steam=" + member.SteamId +
            " name=" + NullToDash(member.PlayerName) +
            " steamName=" + NullToDash(member.SteamName) +
            " slot=" + member.PlayerSlot +
            " state=" + member.State +
            " mod=" + member.ModInstalled +
            " floor=" + Short(member.CurrentFloorId) +
            " checkpoint=" + Short(member.CheckpointId);
    }

    private static string DescribeCheckpoint(ReconnectCheckpointRecord checkpoint)
    {
        if (checkpoint == null)
        {
            return "<null>";
        }

        return "id=" + NullToDash(checkpoint.CheckpointId) +
            " floor=" + NullToDash(checkpoint.FloorName) +
            " guid=" + Short(checkpoint.FloorGuid) +
            " run=" + NullToDash(checkpoint.RunId) +
            " slot=" + NullToDash(checkpoint.SaveSlotId) +
            " chapter=" + NullToDash(checkpoint.LobbyChapter) +
            " genPlayers=" + checkpoint.GenerationPlayerCount +
            " genLuck=" + checkpoint.GenerationMaxLuck +
            " hash=" + Short(checkpoint.CheckpointHash);
    }

    private static string DescribeHello(ReconnectHelloMessage msg)
    {
        return "steam=" + msg.playerSteamId +
            " name=" + NullToDash(msg.playerName) +
            " reconnect=" + msg.reconnectAttempt +
            " protocol=" + NullToDash(msg.protocolVersion) +
            " save=" + NullToDash(msg.saveSlotId) +
            " run=" + NullToDash(msg.runId) +
            " session=" + Short(msg.sessionId) +
            " token=" + Short(msg.reconnectToken) +
            " checkpoint=" + Short(msg.checkpointId);
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

        ReconnectLogger.Info(message);
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
