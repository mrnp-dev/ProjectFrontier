#if UNITY_EDITOR
using System.IO;
using TMPro;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ProjectFrontier.Editor
{
    /// <summary>
    /// Deterministically bakes 16-PPU Point-filtered PNG sprites into Assets/Art/Generated/
    /// and builds Milestone 1 (Oakhaven Frontier Village + touch controls + NPC chatbox + graphics presets)
    /// inside Assets/Scenes/SampleScene.unity.
    /// </summary>
    public static class FrontierSceneBuilder
    {
        private const string GeneratedArtDir = "Assets/Art/Generated";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        private static Sprite talkBubble;

        [MenuItem("ProjectFrontier/Rebuild Milestone 1 Village Scene")]
        public static void BuildMilestone1Scene()
        {
            // Ensure crisp 16-PPU sprite PNG assets exist on disk so Edit Mode, Play Mode, and Builds all serialize them.
            Directory.CreateDirectory(GeneratedArtDir);
            Sprite grass0 = SaveSpriteAsset("tile_grass_0", PixelArtFactory.GetGrassTile(0));
            Sprite grass1 = SaveSpriteAsset("tile_grass_1", PixelArtFactory.GetGrassTile(1));
            Sprite grass2 = SaveSpriteAsset("tile_grass_2", PixelArtFactory.GetGrassTile(2));
            Sprite dirt0 = SaveSpriteAsset("tile_dirt_0", PixelArtFactory.GetDirtTile(0));
            Sprite dirt1 = SaveSpriteAsset("tile_dirt_1", PixelArtFactory.GetDirtTile(1));
            Sprite cobble = SaveSpriteAsset("tile_cobble", PixelArtFactory.GetCobbleTile());

            Sprite heroBody = SaveSpriteAsset("hero_body", PixelArtFactory.GetCharacterBodySprite("hero", FrontierPalette.FrontierTeal, FrontierPalette.TimberDark));
            Sprite heroHead = SaveSpriteAsset("hero_head", PixelArtFactory.GetCharacterHeadSprite("hero", FrontierPalette.TimberDark, FrontierPalette.Terracotta));
            Sprite elderBody = SaveSpriteAsset("elder_body", PixelArtFactory.GetCharacterBodySprite("elder", FrontierPalette.ElderIndigo, FrontierPalette.BrassBorder));
            Sprite elderHead = SaveSpriteAsset("elder_head", PixelArtFactory.GetCharacterHeadSprite("elder", FrontierPalette.CreamParchment, FrontierPalette.ElderIndigo));
            Sprite scoutBody = SaveSpriteAsset("scout_body", PixelArtFactory.GetCharacterBodySprite("scout", FrontierPalette.Terracotta, FrontierPalette.TimberDark));
            Sprite scoutHead = SaveSpriteAsset("scout_head", PixelArtFactory.GetCharacterHeadSprite("scout", FrontierPalette.TimberDark, FrontierPalette.MeadowMoss));

            Sprite foot = SaveSpriteAsset("char_foot", PixelArtFactory.GetCharacterFootSprite());
            Sprite heroScarf = SaveSpriteAsset("hero_scarf", PixelArtFactory.GetScarfSprite(FrontierPalette.Terracotta));
            Sprite shadow = SaveSpriteAsset("char_shadow", PixelArtFactory.GetGroundShadowSprite());

            Sprite lanternShard = SaveSpriteAsset("pickup_lantern", PixelArtFactory.GetLanternShardSprite());
            Sprite cottage = SaveSpriteAsset("prop_cottage", PixelArtFactory.GetCottageSprite());
            Sprite pine = SaveSpriteAsset("prop_pine", PixelArtFactory.GetPineTreeSprite());
            Sprite lanternPost = SaveSpriteAsset("prop_lanternpost", PixelArtFactory.GetLanternPostSprite());
            Sprite hearth = SaveSpriteAsset("prop_hearth", PixelArtFactory.GetHearthBrazierSprite());
            Sprite fence = SaveSpriteAsset("prop_fence", PixelArtFactory.GetFenceSprite());

            Sprite uiSolid = SaveSpriteAsset("ui_solid", PixelArtFactory.GetSolidUiSprite());
            talkBubble = SaveSpriteAsset("ui_talk_bubble", PixelArtFactory.GetTalkBubbleSprite());
            Sprite uiPanel = SaveSpriteAsset("ui_panel", PixelArtFactory.GetUiPanelSprite(), border: new Vector4(4, 4, 4, 4));
            Sprite uiJoyRing = SaveSpriteAsset("ui_joy_ring", PixelArtFactory.GetCircleUiSprite("ui_joy_ring", 48, FrontierPalette.WithAlpha(FrontierPalette.SlatePanel, 0.55f), FrontierPalette.BrassBorder));
            Sprite uiJoyKnob = SaveSpriteAsset("ui_joy_knob", PixelArtFactory.GetCircleUiSprite("ui_joy_knob", 28, FrontierPalette.HearthGold, FrontierPalette.InkDark));

            // Create a dedicated URP 2D Sprite Lit material so zero renderers ever touch Default-Material.
            Material spriteLitMat = EnsureSpriteMaterial("Assets/Art/Generated/FrontierSpriteLit.mat", "Universal Render Pipeline/2D/Sprite-Lit-Default");
            Material spriteUnlitMat = EnsureSpriteMaterial("Assets/Art/Generated/FrontierSpriteUnlit.mat", "Universal Render Pipeline/2D/Sprite-Unlit-Default");

            // Open SampleScene and preserve baseline Main Camera, Global Light 2D, Global Volume.
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            CleanupPreviousBuildRoots(scene);

            // Ensure Main Camera has CinemachineBrain + PixelPerfectCamera (480x270 @ 16 PPU)
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                mainCam = camGo.AddComponent<Camera>();
            }
            mainCam.orthographic = true;
            mainCam.orthographicSize = 8.4375f; // 270 / (2 * 16)
            mainCam.backgroundColor = FrontierPalette.DeepPine;
            mainCam.allowMSAA = false;
            mainCam.allowDynamicResolution = false;
            mainCam.transform.position = new Vector3(0f, 0f, -10f);
            if (mainCam.GetComponent<AudioListener>() == null) mainCam.gameObject.AddComponent<AudioListener>();
            var ppCam = mainCam.GetComponent<PixelPerfectCamera>() ?? mainCam.gameObject.AddComponent<PixelPerfectCamera>();
            ppCam.assetsPPU = 16;
            ppCam.refResolutionX = 480;
            ppCam.refResolutionY = 270;
            ppCam.cropFrame = PixelPerfectCamera.CropFrame.None; // fill any phone aspect; no letter/pillar boxes
            var brain = mainCam.GetComponent<CinemachineBrain>() ?? mainCam.gameObject.AddComponent<CinemachineBrain>();
            brain.UpdateMethod = CinemachineBrain.UpdateMethods.LateUpdate;

            // Ensure Managers root
            var managersGo = new GameObject("GameSystems");
            var gameLoop = managersGo.AddComponent<FrontierGameLoop>();
            var gfxMgr = managersGo.AddComponent<GraphicsSettingsManager>();
            var audioMgr = managersGo.AddComponent<SynthAudioManager>();
            var feelMgr = managersGo.AddComponent<FeelManager>();
            var liveMgr = managersGo.AddComponent<FrontierLiveManager>();
            var impulseSrc = managersGo.AddComponent<CinemachineImpulseSource>();
            feelMgr.impulseSource = impulseSrc;

            // Assign URP tier assets via SerializedObject on GraphicsSettingsManager
            var soGfx = new SerializedObject(gfxMgr);
            soGfx.FindProperty("lowAsset").objectReferenceValue = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Graphics/URP2D_Low.asset");
            soGfx.FindProperty("mediumAsset").objectReferenceValue = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Graphics/URP2D_Medium.asset");
            soGfx.FindProperty("highAsset").objectReferenceValue = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Graphics/URP2D_High.asset");
            soGfx.ApplyModifiedPropertiesWithoutUndo();

            // Build Oakhaven Village World
            var worldRoot = new GameObject("VillageWorld");
            BuildVillageGround(worldRoot.transform, new[] { grass0, grass1, grass2 }, new[] { dirt0, dirt1 }, cobble, spriteLitMat);
            BuildVillageProps(worldRoot.transform, cottage, pine, lanternPost, hearth, fence, spriteLitMat);

            // Build Hero Player
            var playerGo = BuildCharacter("HeroPlayer", new Vector3(0f, -1.6f, 0f), heroBody, heroHead, foot, heroScarf, shadow, spriteLitMat, sortBase: 20);
            var rb = playerGo.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            var playerCol = playerGo.AddComponent<CircleCollider2D>();
            playerCol.radius = 0.4f;
            var playerCtrl = playerGo.AddComponent<PlayerController2D>();

            // Build NPCs (Elder Rowan & Scout Mira)
            BuildNpc(worldRoot.transform, "Npc_ElderRowan", new Vector3(0f, 1.4f, 0f), elderBody, elderHead, foot, shadow, spriteLitMat, spriteUnlitMat, uiPanel,
                "elder_rowan", "Elder Rowan", "Hearth Keeper",
                new[]
                {
                    "Welcome to Oakhaven! Dusk falls fast across the frontier.",
                    "Gather 5 Lantern Shards before the timer runs out—here, take my spare spark!"
                },
                grantsBonusShard: true);

            BuildNpc(worldRoot.transform, "Npc_ScoutMira", new Vector3(5.6f, -0.5f, 0f), scoutBody, scoutHead, foot, shadow, spriteLitMat, spriteUnlitMat, uiPanel,
                "scout_mira", "Scout Mira", "Trail Watcher",
                new[]
                {
                    "The timber cottages stay crisp at 480x270 on even modest Android phones.",
                    "Tap GFX in the corner anytime to switch Low, Medium, High, or Custom presets!"
                },
                grantsBonusShard: false);

            // Build 5 Lantern Shard Pickups around the village square
            Vector3[] shardPositions =
            {
                new Vector3(-3.2f, -1.4f, 0f),
                new Vector3(3.4f, -1.4f, 0f),
                new Vector3(-4.5f, 2.5f, 0f),
                new Vector3(4.2f, 2.8f, 0f),
                new Vector3(0f, -4.4f, 0f)
            };
            for (int i = 0; i < shardPositions.Length; i++)
            {
                BuildLanternShard(worldRoot.transform, $"LanternShard_{i + 1}", shardPositions[i], lanternShard, shadow, spriteUnlitMat);
            }

            // Build Cinemachine 3 Virtual Camera + Rig
            var vcamGo = new GameObject("FrontierVirtualCamera");
            vcamGo.transform.position = new Vector3(0f, 0f, -10f);
            var vcam = vcamGo.AddComponent<CinemachineCamera>();
            vcam.Lens.OrthographicSize = 8.4375f;
            vcam.Lens.NearClipPlane = 0.1f;
            vcam.Lens.FarClipPlane = 50f;
            var composer = vcamGo.AddComponent<CinemachinePositionComposer>();
            composer.Damping = new Vector3(0.45f, 0.45f, 0f);
            composer.CameraDistance = 10f;
            vcamGo.AddComponent<CinemachineImpulseListener>();

            var rig = vcamGo.AddComponent<FrontierCameraRig>();
            rig.playerTarget = playerGo.transform;
            rig.virtualCamera = vcam;

            // Build EventSystem (New Input System UI Module) + uGUI Shell Canvas
            BuildEventSystem();
            BuildShellCanvas(feelMgr, uiSolid, uiPanel, uiJoyRing, uiJoyKnob);

            // Register SampleScene in Build Settings so SceneManager.LoadScene(0) works in Play Mode & Builds
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            // Cleanly enforce Android minSdk 25+ (Unity 6 minimum)
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        private static void CleanupPreviousBuildRoots(Scene scene)
        {
            string[] managedNames = { "GameSystems", "VillageWorld", "HeroPlayer", "FrontierVirtualCamera", "FrontierCanvas", "EventSystem", "CameraTargetAnchor" };
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var target in managedNames)
                {
                    if (root.name == target)
                    {
                        Object.DestroyImmediate(root);
                        break;
                    }
                }
            }
        }

        private static void BuildVillageGround(Transform parent, Sprite[] grass, Sprite[] dirt, Sprite cobble, Material mat)
        {
            var groundRoot = new GameObject("GroundTiles");
            groundRoot.transform.SetParent(parent, false);

            // One tiled renderer under the authored tiles so wide phones / camera edges never show void.
            var underlayGo = new GameObject("GrassUnderlay");
            underlayGo.transform.SetParent(groundRoot.transform, false);
            underlayGo.transform.position = new Vector3(0f, 0f, 0.1f);
            var underlay = underlayGo.AddComponent<SpriteRenderer>();
            underlay.sprite = grass[0];
            underlay.sharedMaterial = mat;
            underlay.drawMode = SpriteDrawMode.Tiled;
            underlay.tileMode = SpriteTileMode.Continuous;
            underlay.size = new Vector2(64f, 40f);
            underlay.sortingOrder = -101;
            for (int y = -9; y <= 9; y++)
            for (int x = -15; x <= 15; x++)
            {
                bool isPlaza = Mathf.Abs(x) <= 2 && Mathf.Abs(y) <= 2;
                bool isCrossPath = (Mathf.Abs(x) <= 1 && y >= -7 && y <= 6) || (Mathf.Abs(y) <= 1 && x >= -10 && x <= 10);
                Sprite tile;
                if (isPlaza) tile = cobble;
                else if (isCrossPath) tile = dirt[Mathf.Abs(x * 7 + y * 13) % dirt.Length];
                else tile = grass[Mathf.Abs(x * 11 + y * 5) % grass.Length];

                var tileGo = new GameObject($"Tile_{x}_{y}");
                tileGo.transform.SetParent(groundRoot.transform, false);
                tileGo.transform.position = new Vector3(x, y, 0f);
                var sr = tileGo.AddComponent<SpriteRenderer>();
                sr.sprite = tile;
                sr.sharedMaterial = mat;
                sr.sortingOrder = -100;
            }
        }

        private static void BuildVillageProps(Transform parent, Sprite cottage, Sprite pine, Sprite lanternPost, Sprite hearth, Sprite fence, Material mat)
        {
            var propsRoot = new GameObject("VillageProps");
            propsRoot.transform.SetParent(parent, false);

            // Central Hearth Brazier + warm 2D point light
            var hearthGo = SpawnProp(propsRoot.transform, "VillageHearth", new Vector3(0f, 0.2f, 0f), hearth, mat, sortOrder: 5, addCollider: true);
            AddPointLight2D(hearthGo.transform, new Vector3(0f, 0.25f, 0f), FrontierPalette.HearthGold, 1.35f, 4.6f);

            // Cottages framing the square
            SpawnProp(propsRoot.transform, "Cottage_West", new Vector3(-6.2f, 3.8f, 0f), cottage, mat, sortOrder: 8, addCollider: true);
            SpawnProp(propsRoot.transform, "Cottage_East", new Vector3(6.2f, 3.8f, 0f), cottage, mat, sortOrder: 8, addCollider: true);
            SpawnProp(propsRoot.transform, "Cottage_SouthWest", new Vector3(-6.5f, -4.2f, 0f), cottage, mat, sortOrder: 12, addCollider: true);
            SpawnProp(propsRoot.transform, "Cottage_SouthEast", new Vector3(6.5f, -4.2f, 0f), cottage, mat, sortOrder: 12, addCollider: true);

            // Lantern posts with toggleable 2D point lights (respects GraphicsSettingsManager Low/Med/High)
            Vector3[] postCoords = { new Vector3(-2.6f, 2.2f, 0f), new Vector3(2.6f, 2.2f, 0f), new Vector3(-2.6f, -2.5f, 0f), new Vector3(2.6f, -2.5f, 0f) };
            for (int i = 0; i < postCoords.Length; i++)
            {
                var lp = SpawnProp(propsRoot.transform, $"LanternPost_{i + 1}", postCoords[i], lanternPost, mat, sortOrder: 10, addCollider: false);
                AddPointLight2D(lp.transform, new Vector3(0.18f, 0.35f, 0f), FrontierPalette.HearthGold, 1.05f, 3.6f);
            }

            // Pine tree perimeter
            Vector3[] pineCoords =
            {
                new Vector3(-11.5f, 6.2f, 0f), new Vector3(-9.5f, 6.6f, 0f), new Vector3(-3.2f, 6.4f, 0f),
                new Vector3(3.2f, 6.4f, 0f), new Vector3(9.5f, 6.6f, 0f), new Vector3(11.5f, 6.2f, 0f),
                new Vector3(-11.8f, 1.5f, 0f), new Vector3(11.8f, 1.5f, 0f),
                new Vector3(-11.5f, -6.2f, 0f), new Vector3(-9.2f, -6.5f, 0f), new Vector3(9.2f, -6.5f, 0f), new Vector3(11.5f, -6.2f, 0f)
            };
            for (int i = 0; i < pineCoords.Length; i++)
            {
                SpawnProp(propsRoot.transform, $"PineTree_{i + 1}", pineCoords[i], pine, mat, sortOrder: 14, addCollider: false);
            }

            // Timber fences along north/south road edges
            for (int x = -8; x <= 8; x += 2)
            {
                if (Mathf.Abs(x) <= 2) continue;
                SpawnProp(propsRoot.transform, $"FenceN_{x}", new Vector3(x, 5.5f, 0f), fence, mat, sortOrder: 6, addCollider: false);
                SpawnProp(propsRoot.transform, $"FenceS_{x}", new Vector3(x, -6.0f, 0f), fence, mat, sortOrder: 6, addCollider: false);
            }
        }

        private static GameObject SpawnProp(Transform parent, string name, Vector3 pos, Sprite sprite, Material mat, int sortOrder, bool addCollider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = mat;
            sr.sortingOrder = sortOrder;
            if (addCollider)
            {
                var box = go.AddComponent<BoxCollider2D>();
                box.size = new Vector2(sprite.bounds.size.x * 0.72f, sprite.bounds.size.y * 0.42f);
                box.offset = new Vector2(0f, -sprite.bounds.size.y * 0.2f);
            }
            return go;
        }

        private static void AddPointLight2D(Transform parent, Vector3 localPos, Color color, float intensity, float outerRadius)
        {
            var lGo = new GameObject("PointLight2D");
            lGo.transform.SetParent(parent, false);
            lGo.transform.localPosition = localPos;
            var l2d = lGo.AddComponent<Light2D>();
            l2d.lightType = Light2D.LightType.Point;
            l2d.color = color;
            l2d.intensity = intensity;
            l2d.pointLightInnerRadius = outerRadius * 0.25f;
            l2d.pointLightOuterRadius = outerRadius;
            l2d.falloffIntensity = 0.65f;
        }

        private static GameObject BuildCharacter(string name, Vector3 pos, Sprite body, Sprite head, Sprite foot, Sprite scarf, Sprite shadow, Material mat, int sortBase)
        {
            var root = new GameObject(name);
            root.transform.position = pos;
            var visualRoot = new GameObject("VisualRoot");
            visualRoot.transform.SetParent(root.transform, false);

            var shGo = MakeSpriteChild(visualRoot.transform, "GroundShadow", shadow, mat, new Vector3(0f, -0.44f, 0f), sortBase - 2);
            var lFoot = MakeSpriteChild(visualRoot.transform, "LeftFoot", foot, mat, new Vector3(-0.18f, -0.34f, 0f), sortBase);
            var rFoot = MakeSpriteChild(visualRoot.transform, "RightFoot", foot, mat, new Vector3(0.18f, -0.34f, 0f), sortBase);
            var bodyGo = MakeSpriteChild(visualRoot.transform, "Body", body, mat, new Vector3(0f, 0f, 0f), sortBase + 1);
            var headGo = MakeSpriteChild(visualRoot.transform, "Head", head, mat, new Vector3(0f, 0.56f, 0f), sortBase + 2);
            GameObject scarfGo = scarf != null ? MakeSpriteChild(bodyGo.transform, "Scarf", scarf, mat, new Vector3(-0.34f, 0.16f, 0f), sortBase) : null;

            var motion = root.AddComponent<CharacterMotion2D>();
            motion.visualRoot = visualRoot.transform;
            motion.bodyTransform = bodyGo.transform;
            motion.headTransform = headGo.transform;
            motion.leftFootTransform = lFoot.transform;
            motion.rightFootTransform = rFoot.transform;
            motion.scarfTransform = scarfGo != null ? scarfGo.transform : null;
            return root;
        }

        private static void BuildNpc(Transform parent, string name, Vector3 pos, Sprite body, Sprite head, Sprite foot, Sprite shadow, Material litMat, Material unlitMat, Sprite uiPanel,
            string npcId, string displayName, string role, string[] lines, bool grantsBonusShard)
        {
            var npcGo = BuildCharacter(name, pos, body, head, foot, null, shadow, litMat, sortBase: 18);
            npcGo.transform.SetParent(parent, true);

            // Pixel-art speech bubble talk indicator (world-space TMP smears at 480x270).
            var promptGo = MakeSpriteChild(npcGo.transform, "TalkPrompt", talkBubble, unlitMat, new Vector3(0f, 1.3f, 0f), 45);

            var npc = npcGo.AddComponent<VillageNpc>();
            npc.NpcId = npcId;
            npc.DisplayName = displayName;
            npc.RoleTitle = role;
            npc.DialogueLines = lines;
            npc.grantsBonusShardOnFirstTalk = grantsBonusShard;
            npc.promptIndicator = promptGo.transform;
        }

        private static void BuildLanternShard(Transform parent, string name, Vector3 pos, Sprite sprite, Sprite shadow, Material unlitMat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            MakeSpriteChild(go.transform, "Shadow", shadow, unlitMat, new Vector3(0f, -0.45f, 0f), 15);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = unlitMat;
            sr.sortingOrder = 16;
            var col = go.AddComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = 0.55f;
            go.AddComponent<LanternShardPickup>();
            AddPointLight2D(go.transform, Vector3.zero, FrontierPalette.HearthGold, 0.85f, 2.4f);
        }

        private static GameObject MakeSpriteChild(Transform parent, string name, Sprite sprite, Material mat, Vector3 localPos, int sortOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = mat;
            sr.sortingOrder = sortOrder;
            return go;
        }

        private static void BuildEventSystem()
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<InputSystemUIInputModule>();
        }

        private static void BuildShellCanvas(FeelManager feelMgr, Sprite uiSolid, Sprite uiPanel, Sprite uiJoyRing, Sprite uiJoyKnob)
        {
            var canvasGo = new GameObject("FrontierCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var shell = canvasGo.AddComponent<FrontierShellUI>();

            // 1. TITLE SCREEN (live village visible behind soft scrim)
            var titleRoot = CreateFullRect(canvasGo.transform, "TitleScreen");
            var titleScrim = CreateImage(titleRoot.transform, "TitleScrim", uiSolid, FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.42f), raycast: false);
            StretchFill(titleScrim.rectTransform);

            var titleBanner = CreateImage(titleRoot.transform, "TitleCard", uiPanel, FrontierPalette.WithAlpha(FrontierPalette.SlatePanel, 0.88f), raycast: false);
            titleBanner.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(titleBanner.rectTransform, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 150f));

            var titleTxt = CreateStyledTmp(titleBanner.transform, "GameTitleText", "PROJECT FRONTIER", 54f, FrontierPalette.HearthGold, TextAlignmentOptions.Center);
            titleTxt.textWrappingMode = TextWrappingModes.NoWrap;
            SetAnchored(titleTxt.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(720f, 76f));
            var subtitleTxt = CreateStyledTmp(titleBanner.transform, "SubtitleText", "OAKHAVEN VILLAGE · 16-PPU ANDROID VERTICAL SLICE", 18f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            SetAnchored(subtitleTxt.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(720f, 30f));

            var startBtn = CreateStyledButton(titleRoot.transform, "TitleStartButton", uiPanel, "TAP OR PRESS ANY KEY TO START", 24f, FrontierPalette.WithAlpha(FrontierPalette.SlatePanel, 0.88f), FrontierPalette.CreamParchment, out var startLabel);
            SetAnchored(startBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.24f), new Vector2(0.5f, 0.24f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 68f));

            shell.titleRoot = titleRoot;
            shell.titlePromptText = startLabel;
            shell.titleStartButton = startBtn;

            // 2. GAMEPLAY HUD
            var hudRoot = CreateFullRect(canvasGo.transform, "GameplayHUD");
            shell.hudRoot = hudRoot;

            // Top-Left Goal & Progress Card
            var goalCard = CreateImage(hudRoot.transform, "GoalCard", uiPanel, FrontierPalette.WithAlpha(FrontierPalette.SlatePanel, 0.90f), raycast: false);
            goalCard.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(goalCard.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -20f), new Vector2(430f, 108f));

            shell.goalText = CreateStyledTmp(goalCard.transform, "GoalText", "LANTERNS 0/5 · LIGHT THE HEARTH", 20f, FrontierPalette.HearthGold, TextAlignmentOptions.Left);
            shell.goalText.textWrappingMode = TextWrappingModes.NoWrap;
            shell.goalText.enableAutoSizing = true;
            shell.goalText.fontSizeMin = 14f;
            shell.goalText.fontSizeMax = 20f;
            SetAnchored(shell.goalText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(-28f, 32f));

            var barBg = CreateImage(goalCard.transform, "ProgressBarBg", uiSolid, FrontierPalette.InkDark, raycast: false);
            SetAnchored(barBg.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -46f), new Vector2(-28f, 16f));
            var barFill = CreateImage(barBg.transform, "ProgressBarFill", uiSolid, FrontierPalette.HearthGold, raycast: false);
            barFill.type = UnityEngine.UI.Image.Type.Filled;
            barFill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            barFill.fillAmount = 0f;
            StretchFill(barFill.rectTransform, 2f, 2f);
            shell.progressFillImage = barFill;

            shell.timerText = CreateStyledTmp(goalCard.transform, "TimerText", "DUSK 01:30", 18f, FrontierPalette.CreamParchment, TextAlignmentOptions.Left);
            SetAnchored(shell.timerText.rectTransform, new Vector2(0f, 0f), new Vector2(0.6f, 0f), new Vector2(0f, 0f), new Vector2(14f, 10f), new Vector2(220f, 28f));

            // Top-Right Controls (GFX Badge + Pause)
            var gfxBtn = CreateStyledButton(hudRoot.transform, "HudGfxButton", uiPanel, "GFX: LOW", 18f, FrontierPalette.SlatePanel, FrontierPalette.HearthGold, out var gfxLbl);
            SetAnchored(gfxBtn.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-150f, -20f), new Vector2(150f, 54f));
            shell.hudSettingsButton = gfxBtn;
            shell.presetBadgeText = gfxLbl;

            var pauseBtn = CreateStyledButton(hudRoot.transform, "HudPauseButton", uiPanel, "PAUSE", 18f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(pauseBtn.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -20f), new Vector2(114f, 54f));
            shell.hudPauseButton = pauseBtn;

            // Bottom-Left Touch Virtual Joystick
            var joyBg = CreateImage(hudRoot.transform, "TouchJoystickBg", uiJoyRing, Color.white, raycast: true);
            SetAnchored(joyBg.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(36f, 34f), new Vector2(150f, 150f));
            var joyKnob = CreateImage(joyBg.transform, "TouchJoystickKnob", uiJoyKnob, Color.white, raycast: false);
            SetAnchored(joyKnob.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64f, 64f));
            var joyCtrl = joyBg.gameObject.AddComponent<VirtualJoystickUI>();
            joyCtrl.background = joyBg.rectTransform;
            joyCtrl.handle = joyKnob.rectTransform;
            joyCtrl.maxRadius = 46f;

            // Bottom-Right Touch Interact Button
            var talkBtn = CreateStyledButton(hudRoot.transform, "TouchInteractButton", uiPanel, "TALK", 22f, FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, out var talkLbl);
            SetAnchored(talkBtn.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-36f, 44f), new Vector2(148f, 92f));
            shell.hudInteractButton = talkBtn;
            shell.hudInteractLabel = talkLbl;

            // NPC Chatbox Popup
            var dlgPanel = CreateImage(hudRoot.transform, "NpcChatbox", uiPanel, FrontierPalette.WithAlpha(FrontierPalette.SlatePanel, 0.96f), raycast: true);
            dlgPanel.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(dlgPanel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(720f, 172f));
            shell.dialogueRoot = dlgPanel.gameObject;

            shell.dialogueSpeakerText = CreateStyledTmp(dlgPanel.transform, "SpeakerName", "ELDER ROWAN", 22f, FrontierPalette.HearthGold, TextAlignmentOptions.Left);
            SetAnchored(shell.dialogueSpeakerText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(-36f, 32f));

            shell.dialogueBodyText = CreateStyledTmp(dlgPanel.transform, "DialogueBody", "Welcome to Oakhaven!", 20f, FrontierPalette.CreamParchment, TextAlignmentOptions.TopLeft);
            shell.dialogueBodyText.textWrappingMode = TextWrappingModes.Normal;
            SetAnchored(shell.dialogueBodyText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-55f, -14f), new Vector2(-150f, -64f));

            var dlgNextBtn = CreateStyledButton(dlgPanel.transform, "DialogueNextButton", uiPanel, "NEXT", 20f, FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, out var dlgNextLbl);
            SetAnchored(dlgNextBtn.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-18f, 18f), new Vector2(118f, 48f));
            shell.dialogueNextButton = dlgNextBtn;
            shell.dialogueNextLabel = dlgNextLbl;
            dlgPanel.gameObject.SetActive(false);

            // 3. PAUSE & GRAPHICS/AUDIO SETTINGS SCREEN
            var pauseRoot = CreateFullRect(canvasGo.transform, "PauseScreen");
            var pauseScrim = CreateImage(pauseRoot.transform, "PauseScrim", uiSolid, FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.72f), raycast: true);
            StretchFill(pauseScrim.rectTransform);

            var pauseCard = CreateImage(pauseRoot.transform, "PauseCard", uiPanel, FrontierPalette.SlatePanel, raycast: true);
            pauseCard.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(pauseCard.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(680f, 600f));

            var pauseHeader = CreateStyledTmp(pauseCard.transform, "PauseTitle", "PAUSED · SETTINGS & GRAPHICS", 30f, FrontierPalette.HearthGold, TextAlignmentOptions.Center);
            SetAnchored(pauseHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(620f, 42f));

            // Graphics Preset Buttons row
            shell.presetLowButton = CreateStyledButton(pauseCard.transform, "PresetLowBtn", uiPanel, "LOW", 18f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.presetLowButton.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-216f, -74f), new Vector2(132f, 46f));
            shell.presetMedButton = CreateStyledButton(pauseCard.transform, "PresetMedBtn", uiPanel, "MEDIUM", 18f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.presetMedButton.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-72f, -74f), new Vector2(132f, 46f));
            shell.presetHighButton = CreateStyledButton(pauseCard.transform, "PresetHighBtn", uiPanel, "HIGH", 18f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.presetHighButton.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(72f, -74f), new Vector2(132f, 46f));
            shell.presetCustomButton = CreateStyledButton(pauseCard.transform, "PresetCustomBtn", uiPanel, "CUSTOM", 18f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.presetCustomButton.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(216f, -74f), new Vector2(132f, 46f));

            shell.presetDetailsText = CreateStyledTmp(pauseCard.transform, "PresetDetails", "LOW · 30 FPS · 2D Lights: OFF · PostFX: OFF · FX: 25%", 15f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            SetAnchored(shell.presetDetailsText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -128f), new Vector2(640f, 28f));

            // Custom Toggles 2x2 Grid (FPS, 2D Lights, Post-FX, Particle Density)
            var customGrp = new GameObject("CustomGraphicsGroup", typeof(RectTransform));
            customGrp.transform.SetParent(pauseCard.transform, false);
            SetAnchored(customGrp.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -162f), new Vector2(600f, 92f));
            shell.customGraphicsGroup = customGrp;
            shell.customFpsButton = CreateStyledButton(customGrp.transform, "CustomFpsBtn", uiPanel, "FPS: 30", 16f, FrontierPalette.DeepPine, FrontierPalette.CreamParchment, out shell.customFpsLabel);
            SetAnchored(shell.customFpsButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-148f, 23f), new Vector2(284f, 40f));
            shell.customLightsButton = CreateStyledButton(customGrp.transform, "CustomLightsBtn", uiPanel, "2D LIGHTS: OFF", 16f, FrontierPalette.DeepPine, FrontierPalette.CreamParchment, out shell.customLightsLabel);
            SetAnchored(shell.customLightsButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(148f, 23f), new Vector2(284f, 40f));
            shell.customPostFxButton = CreateStyledButton(customGrp.transform, "CustomPostFxBtn", uiPanel, "POST-FX: OFF", 16f, FrontierPalette.DeepPine, FrontierPalette.CreamParchment, out shell.customPostFxLabel);
            SetAnchored(shell.customPostFxButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-148f, -23f), new Vector2(284f, 40f));
            shell.customParticlesButton = CreateStyledButton(customGrp.transform, "CustomParticlesBtn", uiPanel, "PARTICLES: 25%", 16f, FrontierPalette.DeepPine, FrontierPalette.CreamParchment, out shell.customParticlesLabel);
            SetAnchored(shell.customParticlesButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(148f, -23f), new Vector2(284f, 40f));

            // Music & SFX Sliders
            shell.musicSlider = CreateStyledSlider(pauseCard.transform, "MusicSlider", "AMBIENT / MUSIC VOL", uiSolid, new Vector2(0f, -284f));
            shell.sfxSlider = CreateStyledSlider(pauseCard.transform, "SfxSlider", "SFX VOL", uiSolid, new Vector2(0f, -356f));

            // Pause Navigation Buttons
            shell.resumeButton = CreateStyledButton(pauseCard.transform, "ResumeBtn", uiPanel, "RESUME", 22f, FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.resumeButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-190f, 34f), new Vector2(175f, 58f));
            shell.pauseRestartButton = CreateStyledButton(pauseCard.transform, "PauseRestartBtn", uiPanel, "RESTART", 22f, FrontierPalette.SlatePanel, FrontierPalette.HearthGold, out _);
            SetAnchored(shell.pauseRestartButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(175f, 58f));
            shell.pauseTitleButton = CreateStyledButton(pauseCard.transform, "PauseTitleBtn", uiPanel, "TITLE", 22f, FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.pauseTitleButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(190f, 34f), new Vector2(175f, 58f));
            shell.pauseRoot = pauseRoot;

            // 4. END SCREEN (Won / Lost)
            var endRoot = CreateFullRect(canvasGo.transform, "EndScreen");
            var endScrim = CreateImage(endRoot.transform, "EndScrim", uiSolid, FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.72f), raycast: true);
            StretchFill(endScrim.rectTransform);
            var endCard = CreateImage(endRoot.transform, "EndCard", uiPanel, FrontierPalette.SlatePanel, raycast: true);
            endCard.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(endCard.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 360f));

            shell.endTitleText = CreateStyledTmp(endCard.transform, "EndTitleText", "OAKHAVEN HEARTH LIT!", 38f, FrontierPalette.HearthGold, TextAlignmentOptions.Center);
            SetAnchored(shell.endTitleText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(580f, 56f));
            shell.endStatsText = CreateStyledTmp(endCard.transform, "EndStatsText", "LANTERNS: 5/5", 20f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            SetAnchored(shell.endStatsText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(580f, 132f));

            shell.endRestartButton = CreateStyledButton(endCard.transform, "EndRestartBtn", uiPanel, "PLAY AGAIN", 22f, FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, out _);
            SetAnchored(shell.endRestartButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-118f, 36f), new Vector2(204f, 60f));
            shell.endTitleButton = CreateStyledButton(endCard.transform, "EndTitleBtn", uiPanel, "TITLE", 22f, FrontierPalette.SlatePanel, FrontierPalette.HearthGold, out _);
            SetAnchored(shell.endTitleButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(118f, 36f), new Vector2(204f, 60f));
            shell.endRoot = endRoot;

            // 5. FEEL OVERLAYS (Flash + Milestone Banner + 0.3s Fade)
            var flashImg = CreateImage(canvasGo.transform, "MilestoneFlash", uiSolid, FrontierPalette.WithAlpha(FrontierPalette.HearthGold, 0f), raycast: false);
            StretchFill(flashImg.rectTransform);
            flashImg.gameObject.SetActive(false);

            var bannerImg = CreateImage(canvasGo.transform, "MilestoneBanner", uiPanel, FrontierPalette.SlatePanel, raycast: false);
            bannerImg.type = UnityEngine.UI.Image.Type.Sliced;
            SetAnchored(bannerImg.rectTransform, new Vector2(0.5f, 0.78f), new Vector2(0.5f, 0.78f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(460f, 88f));
            var bannerTmp = CreateStyledTmp(bannerImg.transform, "MilestoneBannerText", "VILLAGE LIT!", 38f, FrontierPalette.HearthGold, TextAlignmentOptions.Center);
            StretchFill(bannerTmp.rectTransform, 10f, 10f);
            bannerImg.gameObject.SetActive(false);

            var fadeImg = CreateImage(canvasGo.transform, "TransitionFade", uiSolid, FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0f), raycast: false);
            StretchFill(fadeImg.rectTransform);
            fadeImg.gameObject.SetActive(false);

            feelMgr.flashOverlay = flashImg;
            feelMgr.floatingTextMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Generated/TmpUiOutline.mat");
            feelMgr.milestoneBannerRoot = bannerImg.rectTransform;
            feelMgr.milestoneBannerText = bannerTmp;
            shell.fadeOverlay = fadeImg;
        }

        private static GameObject CreateFullRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            StretchFill(go.GetComponent<RectTransform>());
            return go;
        }

        private static UnityEngine.UI.Image CreateImage(Transform parent, string name, Sprite sprite, Color color, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        private static TextMeshProUGUI CreateStyledTmp(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = FontStyles.Bold;
            tmp.characterSpacing = 3.5f;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.fontSharedMaterial = EnsureOutlinedTmpMaterial(tmp.font, "Assets/Art/Generated/TmpUiOutline.mat", 0.18f);
            return tmp;
        }

        private static UnityEngine.UI.Button CreateStyledButton(Transform parent, string name, Sprite panelSprite, string label, float fontSize, Color bgColor, Color textColor, out TextMeshProUGUI labelOut)
        {
            var img = CreateImage(parent, name, panelSprite, bgColor, raycast: true);
            img.type = UnityEngine.UI.Image.Type.Sliced;
            var btn = img.gameObject.AddComponent<UnityEngine.UI.Button>();
            btn.targetGraphic = img;
            labelOut = CreateStyledTmp(img.transform, "Label", label, fontSize, textColor, TextAlignmentOptions.Center);
            StretchFill(labelOut.rectTransform, 8f, 6f);
            return btn;
        }

        private static UnityEngine.UI.Slider CreateStyledSlider(Transform parent, string name, string title, Sprite uiSolid, Vector2 pos)
        {
            var container = new GameObject(name + "Group", typeof(RectTransform));
            container.transform.SetParent(parent, false);
            SetAnchored(container.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), pos, new Vector2(540f, 56f));

            var lbl = CreateStyledTmp(container.transform, "Title", title, 16f, FrontierPalette.CreamParchment, TextAlignmentOptions.Left);
            SetAnchored(lbl.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 24f));

            var sliderGo = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Slider));
            sliderGo.transform.SetParent(container.transform, false);
            SetAnchored(sliderGo.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(0f, 24f));

            var bg = CreateImage(sliderGo.transform, "Background", uiSolid, FrontierPalette.InkDark, raycast: false);
            StretchFill(bg.rectTransform, 0f, 6f);

            var fillArea = CreateFullRect(sliderGo.transform, "Fill Area");
            var fill = CreateImage(fillArea.transform, "Fill", uiSolid, FrontierPalette.HearthGold, raycast: false);
            StretchFill(fill.rectTransform, 0f, 6f);

            var handleArea = CreateFullRect(sliderGo.transform, "Handle Slide Area");
            var handle = CreateImage(handleArea.transform, "Handle", uiSolid, FrontierPalette.CreamParchment, raycast: true);
            handle.rectTransform.sizeDelta = new Vector2(18f, 24f);

            var slider = sliderGo.GetComponent<UnityEngine.UI.Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.8f;
            return slider;
        }

        private static void StretchFill(RectTransform rt, float padX = 0f, float padY = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        private static void SetAnchored(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPos, Vector2 sizeDelta)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
        }

        private static Sprite SaveSpriteAsset(string name, Sprite runtimeSprite, Vector4 border = default)
        {
            string path = $"{GeneratedArtDir}/{name}.png";
            byte[] png = runtimeSprite.texture.EncodeToPNG();
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 16f;
                importer.filterMode = FilterMode.Point;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.spriteBorder = border;
                var tis = new TextureImporterSettings();
                importer.ReadTextureSettings(tis);
                tis.spriteMeshType = SpriteMeshType.FullRect; // required for Tiled draw mode, cheap for 16px sprites
                importer.SetTextureSettings(tis);
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static Material EnsureOutlinedTmpMaterial(TMP_FontAsset font, string assetPath, float width)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null)
            {
                mat = new Material(font.material) { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            mat.EnableKeyword("OUTLINE_ON");
            mat.SetFloat("_OutlineWidth", width);
            mat.SetColor("_OutlineColor", FrontierPalette.InkDark);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material EnsureSpriteMaterial(string assetPath, string shaderName)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return existing;
            Shader sh = Shader.Find(shaderName) ?? Shader.Find("Sprites/Default");
            var mat = new Material(sh) { name = Path.GetFileNameWithoutExtension(assetPath) };
            AssetDatabase.CreateAsset(mat, assetPath);
            return mat;
        }
    }
}
#endif
