using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Scaleform;
using MapEditor.API;
using Control = GTA.Control;
using Screen = GTA.UI.Screen;

namespace MapEditor
{
    public partial class MapEditor : Script
    {
        public static bool IsInFreecam;
        private bool _isChoosingObject;

        private readonly NativeMenu _categoriesMenu;
        private readonly NativeMenu _objectsMenu;
        private readonly NativeMenu _searchMenu;
        private readonly NativeMenu _mainMenu;
	    private readonly NativeMenu _formatMenu;
        private readonly NativeMenu _metadataMenu;
	    private readonly NativeMenu _objectInfoMenu;
	    private readonly NativeMenu _settingsMenu;
	    private readonly NativeMenu _currentObjectsMenu;
        private readonly NativeMenu _filepicker;

		/// <summary>
		/// The list of autoloaded maps to pick one to unload from. Only ever opened when more than one map is
		/// loaded: with a single map there is nothing to pick and the row unloads it outright.
		/// </summary>
		private readonly NativeMenu _unloadAutoloadedMenu;

	    private readonly NativeItem _currentEntitiesItem;

		/// <summary>Greyed out until a map has actually been autoloaded, since there is nothing else to unload.</summary>
		private readonly NativeItem _unloadAutoloadedItem;

		/// <summary>Greyed out until an external mod subscribes through <see cref="ModManager.SuscribeMod"/>: the
		/// submenu it opens is a list of those mods, and mods can subscribe at any time after startup.</summary>
		private readonly NativeItem _externalModItem;

        private readonly ObjectPool _menuPool = new ObjectPool();

        private Entity _previewProp;
        private Entity _snappedProp;
        private Entity _selectedProp;

        /// <summary>
        /// Entities picked with Ctrl + Attack. While <see cref="_multiSelectionSnapped"/> is set they follow the
        /// crosshair as one rigid group, each keeping the offset stored at the same index in
        /// <see cref="_multiSelectionOffsets"/>.
        /// </summary>
        private readonly List<Entity> _multiSelection = new List<Entity>();
        private readonly List<Vector3> _multiSelectionOffsets = new List<Vector3>();
        private bool _multiSelectionSnapped;

        private Marker _snappedMarker;
	    private Marker _selectedMarker;

        private Camera _mainCamera;
        private Camera _objectPreviewCamera;

        /// <summary>
        /// Where the player was standing when the freecam went up. Kept as the last place to put them back
        /// down when the freecam is switched off over somewhere there is no ground to be found at all: it is
        /// the one spot they are known to have been standing on solid footing. See
        /// <see cref="ReturnPlayerToGround"/>.
        /// </summary>
        private Vector3 _freecamEntryPosition;

        /// <summary>
        /// Every interior the freecam has found around itself so far, each mapped to the spot it was found at.
        /// All of them are pinned in memory and switched on again on every frame, and they are held until the
        /// freecam goes down. Empty while the freecam is not up. See <see cref="HoldInteriorsAroundCamera"/>.
        /// </summary>
        private readonly Dictionary<int, Vector3> _heldInteriors = new Dictionary<int, Vector3>();

        /// <summary>
        /// The spot the scan pass in progress is being run around: where the camera was when it started, not
        /// where the camera is now. See <see cref="HoldInteriorsAroundCamera"/>.
        /// </summary>
        private Vector3 _interiorScanOrigin;

        /// <summary>How far into <see cref="InteriorScanOffsets"/> the pass in progress has got.</summary>
        private int _interiorScanCursor;

        private readonly Vector3 _objectPreviewPos = new Vector3(1200.133f, 4000.958f, 85.9f);

        private bool _zAxis = true;
	    private bool _controlsRotate;
        private bool _quitWithSearchVisible;

	    private readonly string _crosshairPath;
	    private readonly string _crosshairBluePath;
	    private readonly string _crosshairYellowPath;

	    private bool _savingMap;
	    private bool _hasLoaded;
	    private int _mapObjCounter = 0;
	    private int _markerCounter = 0;

	    private const Relationship DefaultRelationship = Relationship.Companion;

	    private ObjectTypes _currentObjectType;

		/// <summary>
		/// The category whose objects <see cref="_objectsMenu"/> is currently listing. Null while the
		/// player is still on <see cref="_categoriesMenu"/> and has not picked one.
		/// </summary>
		private ObjectCategory _currentCategory;

		/// <summary>
		/// The DLC <see cref="_objectsMenu"/> is narrowed to, kept across categories so that the choice
		/// sticks while browsing. Reset to <see cref="Dlc.AllName"/> for a category that has no such objects.
		/// </summary>
		private string _dlcFilter = Dlc.AllName;

		/// <summary>
		/// The row that sets <see cref="_dlcFilter"/>, always first in <see cref="_objectsMenu"/>. Null for
		/// a list that offers no filter, i.e. vehicles and peds.
		/// </summary>
		private NativeListItem<string> _dlcFilterItem;

	    private Settings _settings;

	    private readonly string[] _markersTypes = Enum.GetNames(typeof(MarkerType)).ToArray();

        /// <summary>
        /// Step used by every scrollable position/rotation/scale field, in units per input.
        /// Matches the 0.01 granularity of the old NativeUI value lists.
        /// </summary>
        private const float ScrollStep = 0.01f;

        private static readonly int _possibleRange = 1500000;

	    public enum CrosshairType
	    {
		    Crosshair,
			Orb,
			None,
	    }

        // Autosaving
        private int _loadedEntities = 0;
        private int _changesMade = 0;
        private DateTime _lastAutosave = DateTime.Now;

        /// <summary>
        /// Guards the menu Closed handlers so that only a real user "back" triggers the
        /// editor-state cleanup, not our own programmatic show/hide calls.
        /// </summary>
        private bool _programmaticMenuChange;

		public MapEditor()
        {
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;

            if (!Directory.Exists("scripts\\MapEditor"))
                Directory.CreateDirectory("scripts\\MapEditor");

            UserMaps.EnsureFolder();

            ObjectDatabase.SetupRelationships();
			LoadSettings();

		    try
		    {
		        Translation.Load("scripts\\MapEditor", _settings.Translation);
		    }
		    catch (Exception e)
		    {
		        Compat.Notify("~b~~h~Map Editor~h~~w~~n~Failed to load translations. Falling back to English.");
		        Compat.Notify(e.Message);
		    }

            BuildInstructionalButtons();

			// No banner, so LemonUI reserves no space above the subtitle: the menu must sit at
			// the very top of the screen with no negative offset or it draws off-screen.
			_objectInfoMenu = new NativeMenu("", "~b~" + Translation.Translate("PROPERTIES"))
			{
			    Banner = null,
			};
			_objectInfoMenu.Buttons.Visible = false;
			_menuPool.Add(_objectInfoMenu);

			BuildPedComponentsMenu();
			BuildStackingMenu();
			BuildLoopingMenu();

			ModManager.InitMenu();

			_objectsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));

            ObjectDatabase.LoadFromFile("scripts\\ObjectList.ini", ref ObjectDatabase.MainDb);
			ObjectDatabase.LoadInvalidHashes();
			ObjectDatabase.LoadFromFile("scripts\\PedList.ini", ref ObjectDatabase.PedDb);
            ObjectDatabase.LoadFromFile("scripts\\VehicleList.ini", ref ObjectDatabase.VehicleDb);

			// Vehicles and peds are just the game's own hash enums. Without the flat lists (now
			// replaced by the category files) they would start empty, so rebuild them from the enums
			// as a fallback; the category files then reorder them into browsable groups.
			if (ObjectDatabase.VehicleDb.Count == 0 || ObjectDatabase.PedDb.Count == 0)
				ObjectDatabase.LoadEnumDatabases();

			// Must follow the three lists above: the categories are a view over them, and loading one
			// can fold a hand-added model name back into its database.
			ObjectCategories.LoadAll();

		    _crosshairPath = Path.GetFullPath("scripts\\MapEditor\\crosshair.png");
            _crosshairBluePath = Path.GetFullPath("scripts\\MapEditor\\crosshair_blue.png");
            _crosshairYellowPath = Path.GetFullPath("scripts\\MapEditor\\crosshair_yellow.png");

            if (!File.Exists("scripts\\MapEditor\\crosshair.png"))
			    _crosshairPath = Compat.WriteFileFromResources(Assembly.GetExecutingAssembly(), "MapEditor.crosshair.png", "scripts\\MapEditor\\crosshair.png");
            if (!File.Exists("scripts\\MapEditor\\crosshair_blue.png"))
                _crosshairBluePath = Compat.WriteFileFromResources(Assembly.GetExecutingAssembly(), "MapEditor.crosshair_blue.png", "scripts\\MapEditor\\crosshair_blue.png");
            if (!File.Exists("scripts\\MapEditor\\crosshair_yellow.png"))
                _crosshairYellowPath = Compat.WriteFileFromResources(Assembly.GetExecutingAssembly(), "MapEditor.crosshair_yellow.png", "scripts\\MapEditor\\crosshair_yellow.png");

            _categoriesMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));
            _categoriesMenu.ItemActivated += OnCategorySelect;
            _categoriesMenu.Closed += OnCategoriesMenuClosed;
            _menuPool.Add(_categoriesMenu);
            // Search spans every object, so it stays reachable without first entering a category.
            _categoriesMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            RedrawCategoriesMenu();

            // The object list starts empty and is filled from the category the player picks.
            _objectsMenu.ItemActivated += OnObjectSelect;
            _objectsMenu.SelectedIndexChanged += OnIndexChange;
            _objectsMenu.Closed += OnObjectsMenuClosed;
            _menuPool.Add(_objectsMenu);

            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Change Axis"), Control.SelectWeapon));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Zoom"), Control.MoveUpDown));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Favorite"), Control.Context));

            _searchMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));
            _searchMenu.ItemActivated += OnObjectSelect;
            _searchMenu.SelectedIndexChanged += OnIndexChange;
            _searchMenu.Closed += OnSearchMenuClosed;
            _menuPool.Add(_searchMenu);

            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Change Axis"), Control.SelectWeapon));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Zoom"), Control.MoveUpDown));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Favorite"), Control.Context));

            _mainMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("MAIN MENU"));
            _mainMenu.Buttons.Visible = false;
            // Opening the editor at all is the earliest warning that the object list is coming, and it leaves
            // the several seconds the player spends in here to build its rows out of sight.
            _mainMenu.Shown += (sender, args) => BeginObjectRowWarmup();

            var enterExitItem = new NativeItem(Translation.Translate("Enter/Exit Map Editor"));
            enterExitItem.Activated += (sender, args) => ToggleFreecam();
            _mainMenu.Add(enterExitItem);

            var newMapItem = new NativeItem(Translation.Translate("New Map"), Translation.Translate("Remove all current objects and start a new map."));
            newMapItem.Activated += (sender, args) => NewMap();
            _mainMenu.Add(newMapItem);

            var saveMapItem = new NativeItem(Translation.Translate("Save Map"), Translation.Translate("Save all current objects to a file."));
            saveMapItem.Activated += (sender, args) => BeginSaveMap();
            _mainMenu.Add(saveMapItem);

            var loadMapItem = new NativeItem(Translation.Translate("Load Map"), Translation.Translate("Load objects from a file and add them to the world."));
            loadMapItem.Activated += (sender, args) => BeginLoadMap();
            _mainMenu.Add(loadMapItem);

            _unloadAutoloadedItem = new NativeItem(Translation.Translate("Unload Autoloaded Maps"),
                Translation.Translate("Remove the maps that were loaded automatically when the script started."));
            _unloadAutoloadedItem.Activated += (sender, args) => UnloadAutoloadedMaps();
            _mainMenu.Add(_unloadAutoloadedItem);

            _menuPool.Add(_mainMenu);

            _unloadAutoloadedMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("UNLOAD AUTOLOADED MAPS"));
            _unloadAutoloadedMenu.Buttons.Visible = false;
            _unloadAutoloadedMenu.Parent = _mainMenu;
            _menuPool.Add(_unloadAutoloadedMenu);

			_formatMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SELECT FORMAT"));
			_formatMenu.Buttons.Visible = false;
			_formatMenu.Parent = _mainMenu;
	        RedrawFormatMenu();
			_formatMenu.ItemActivated += OnFormatSelect;
			_menuPool.Add(_formatMenu);

            _metadataMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SAVE MAP"));
            _metadataMenu.Buttons.Visible = false;
		    _metadataMenu.Parent = _formatMenu;
		    RedrawMetadataMenu();
            _menuPool.Add(_metadataMenu);

            _filepicker = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PICK FILE"));
            _filepicker.Buttons.Visible = false;
		    _filepicker.Parent = _formatMenu;
            _menuPool.Add(_filepicker);

			_settingsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SETTINGS"));
			BuildSettingsMenu();
			_menuPool.Add(_settingsMenu);

			_currentObjectsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("CURRENT ENTITES"));
	        _currentObjectsMenu.ItemActivated += OnEntityTeleport;
			_currentObjectsMenu.Buttons.Visible = false;
            _menuPool.Add(_currentObjectsMenu);

            // AddSubMenu's string overload sets the right-hand AltTitle, not the row title:
            // the title always comes from the submenu's Subtitle. Set it explicitly instead.
	        _currentEntitiesItem = _mainMenu.AddSubMenu(_currentObjectsMenu);
	        _currentEntitiesItem.Title = Translation.Translate("Current Entities");

	        var settingsItem = _mainMenu.AddSubMenu(_settingsMenu);
	        settingsItem.Title = Translation.Translate("Settings");

	        _externalModItem = _mainMenu.AddSubMenu(ModManager.ModMenu);
	        _externalModItem.Title = Translation.Translate("Create Map for External Mod");

			_mainMenu.SelectedIndex = 0;
			_menuPool.Add(ModManager.ModMenu);
        }

        /// <summary>
        /// Show/hide a menu without tripping the Closed handlers that clean up editor state.
        /// </summary>
        private void SetMenuVisible(NativeMenu menu, bool visible)
        {
            // Showing a menu raises its SelectedIndexChanged, which loads a preview model: anything that
            // throws in there must not leave the flag set, or every Closed handler below is dead from then on.
            _programmaticMenuChange = true;
            try
            {
                menu.Visible = visible;
            }
            finally
            {
                _programmaticMenuChange = false;
            }
        }

        private void CloseAllMenus()
        {
            _programmaticMenuChange = true;
            try
            {
                _menuPool.HideAll();
            }
            finally
            {
                _programmaticMenuChange = false;
            }
        }

        /// <summary>
        /// Leaves the object picker and takes the preview prop with it. The picker owns the camera and
        /// swallows the freecam controls for as long as <see cref="_isChoosingObject"/> is set, so every path
        /// that takes the last picker menu off the screen has to come through here.
        /// </summary>
        private void LeaveObjectPicker()
        {
            _isChoosingObject = false;
            _previewProp?.Delete();
            _previewProp = null;
        }

        /// <summary>
        /// Backing out of the category list is what actually leaves the object picker; the object list
        /// below it only steps back up a level.
        /// </summary>
        private void OnCategoriesMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;
            LeaveObjectPicker();
        }

        private void OnObjectsMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;

            // Back from a category's objects returns to the category list, keeping its selection.
            _previewProp?.Delete();
            _previewProp = null;
            _currentCategory = null;
            SetMenuVisible(_categoriesMenu, true);
        }

        private void OnSearchMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;

            // Search spans every object, so it can be opened straight from the category list. Back out
            // to whichever level it was opened from.
            if (_currentCategory == null)
            {
                SetMenuVisible(_categoriesMenu, true);
                return;
            }

            // Starring a search result while browsing the favorites list changes the very category being
            // backed out into, so it is the one category that cannot just be shown again as it was.
            var favorites = Favorites.CategoryFor(_currentObjectType);
            if (ReferenceEquals(_currentCategory, favorites))
            {
                if (favorites.Objects.Count == 0)
                {
                    _currentCategory = null;
                    SetMenuVisible(_categoriesMenu, true);
                    return;
                }

                RedrawObjectsMenu(favorites, _currentObjectType, _objectsMenu.SelectedIndex);
            }

            SetMenuVisible(_objectsMenu, true);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(0, 0));
            _objectsMenu.Name = "~b~" + _currentCategory.Name.ToUpper();
        }

        /// <summary>
        /// Nothing else will run after this: the script is being reloaded, or it has been aborted over an
        /// unhandled exception. Everything it was holding is game-side and outlives it — a camera still
        /// rendering, a frozen invisible player, a preview prop hanging in the air over the preview spot, and
        /// the whole placed map — while the player is left with no script that can undo any of it. So it is all
        /// given back here.
        ///
        /// The map is the player's work and is not thrown away: it goes to disk first and comes back on the
        /// next start (see <see cref="SessionRestore"/> and <see cref="RestoreSession"/>), as a map the editor
        /// owns again and the player can clear, edit or save. Leaving it standing instead would strand it in the
        /// world, belonging to nothing, until the game itself is restarted.
        /// </summary>
        private void OnAborted(object sender, EventArgs e)
        {
            _previewProp?.Delete();
            _previewProp = null;

            // Before the map, because it is the part the player cannot go on playing without: whatever the rest
            // of this runs into, they have their camera and their character back.
            if (IsInFreecam)
            {
                IsInFreecam = false;

                var player = Game.Player.Character;
                player.IsPositionFrozen = false;
                player.IsVisible = true;

                // Before the cameras go: it works out where to put the player down from where the freecam was
                // left, so it needs the camera to still be there.
                ReturnPlayerToGround();

                World.RenderingCamera = null;
                World.DestroyAllCameras();
                _mainCamera = null;
                _objectPreviewCamera = null;
            }

            // Whatever the freecam was holding in memory goes back to the game. Outside the block above because
            // the freecam can have been switched off already, in the same frame the script went down, before the
            // tick that would have released it ever ran.
            ReleaseHeldInteriors();

            // These come back from their own files on the next start, so anything left standing here is spawned
            // a second time on top of itself.
            AutoloadedMaps.UnloadAll();

            // The map goes to disk and then out of the world, for the next instance of the script to put back.
            // If it could not be written, the world is the only place it still exists and it stays there.
            if (SessionRestore.Save())
            {
                JavascriptHook.StopAllScripts();
                PropStreamer.RemoveAll();
            }
        }

        private void ToggleFreecam()
        {
            ClearMultiSelection();
            IsInFreecam = !IsInFreecam;
            Game.Player.Character.IsPositionFrozen = IsInFreecam;
            Game.Player.Character.IsVisible = !IsInFreecam;
            World.RenderingCamera = null;
            if (!IsInFreecam)
            {
                ReturnPlayerToGround();
                return;
            }
            // Taken before the first tick of freelook moves the player out from under the camera, so it is
            // still the spot they were standing on rather than one the camera dragged them to.
            _freecamEntryPosition = Game.Player.Character.Position;

            World.DestroyAllCameras();
            _mainCamera = World.CreateCamera(GameplayCamera.Position, VectorExtensions.ClampCameraRotation(GameplayCamera.Rotation), 60f);
            _objectPreviewCamera = World.CreateCamera(new Vector3(1200.016f, 3980.998f, 86.05062f), new Vector3(0f, 0f, 0f), 60f);
            World.RenderingCamera = _mainCamera;
        }

        /// <summary>
        /// Sets the player down on the ground under the freecam when it hands them back.
        ///
        /// The camera is what the spot is worked out from, not the player's own position, because the two are
        /// not in the same place: while the freecam is up the player is parked eight metres behind it (see
        /// <see cref="ProcessFreelook"/>) and left frozen there. Out in the open those eight metres are open
        /// air, but in an interior they are usually through a wall or under the floor — outside the shell of
        /// the building, in the empty space the interior sits in, with nothing under them for a very long way.
        /// Put back from there the player falls out of the world, while the camera position is a spot in the
        /// room they were actually working in.
        ///
        /// The drop itself is a probe straight down rather than the height the game reports above ground
        /// (GET_ENTITY_HEIGHT_ABOVE_GROUND, behind <see cref="Entity.HeightAboveGround"/>), which is measured
        /// against the world's own ground and does not see interior floors: in an interior it answers with the
        /// drop to the terrain under the whole building, which again puts the player underneath the floor they
        /// were standing on. A probe hits interior floors, and placed props and vehicles with them.
        ///
        /// If neither probe finds anything, the player goes back to <see cref="_freecamEntryPosition"/>: the
        /// spot the freecam was switched on from, and the last one they are known to have been standing on
        /// solid ground at.
        /// </summary>
        private void ReturnPlayerToGround()
        {
            var player = Game.Player.Character;
            var playerPos = player.Position;
            var hasCamera = _mainCamera != null && _mainCamera.Exists();

            Vector3? landing = null;

            if (hasCamera)
            {
                var cameraPos = _mainCamera.Position;
                var ground = GroundBelow(cameraPos, player);
                if (ground.HasValue) landing = new Vector3(cameraPos.X, cameraPos.Y, ground.Value + 1f);
            }

            if (!landing.HasValue)
            {
                // Nothing under the camera — it was left over a hole in the map, or off the edge of it. The
                // player's own spot is the next best thing. Started a little above them so that a floor they
                // are level with, or slightly sunk into, is in front of the probe rather than behind it.
                var ground = GroundBelow(playerPos + new Vector3(0f, 0f, 0.5f), player);
                if (ground.HasValue) landing = new Vector3(playerPos.X, playerPos.Y, ground.Value + 1f);
            }

            // Nowhere to be found on the ground at all: back where they came in, exactly. Nothing is probed
            // for there — it is a spot they were already standing on, and the point of it is that it is the
            // one place left that is known to be good. Zero means it was never taken, the freecam having gone
            // up some way other than ToggleFreecam, and then there is nothing better than where they are.
            var entry = _freecamEntryPosition != Vector3.Zero ? _freecamEntryPosition : playerPos;
            var target = landing ?? entry;

            // The camera can have carried the player a long way from where they started, so the ground they
            // are being given back to may not be in memory yet. The flag holds them up until it is.
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, target.X, target.Y, target.Z);
            Function.Call(Hash.SET_ENTITY_LOAD_COLLISION_FLAG, player.Handle, true);

            player.Position = target;
        }

        /// <summary>How far out around the camera the close-up half of the scan reaches, and how finely it is
        /// walked. Fine enough to catch a room the size of a shop, and to tell one floor of a tower from the
        /// one above it, which is what the vertical step is for: floors are barely three metres apart.</summary>
        private const float InteriorNearRadius = 25f;
        private const float InteriorNearHeight = 24f;
        private const float InteriorNearStep = 4f;
        private const float InteriorNearStepZ = 3f;

        /// <summary>How far out the wide half of the scan reaches, walked coarsely because it is there to find
        /// whole buildings before the camera arrives at them, not to resolve small rooms inside them. Anything
        /// it misses is picked up by the fine half as the camera comes closer.</summary>
        private const float InteriorFarRadius = 90f;
        private const float InteriorFarHeight = 48f;
        private const float InteriorFarStep = 12f;
        private const float InteriorFarStepZ = 6f;

        /// <summary>Points looked at per frame. The whole grid is some thousands of them, so a pass takes about
        /// half a second at sixty frames, spread thin enough not to be felt.</summary>
        private const int InteriorScanSamplesPerTick = 192;

        /// <summary>How far the camera has to fly before the grid is walked again around where it now is.</summary>
        private const float InteriorRescanDistance = 10f;

        /// <summary>How far behind the camera a held interior has to be left before it is given back to the
        /// game. See <see cref="ForgetDistantInteriors"/>.</summary>
        private const float InteriorForgetDistance = 400f;

        /// <summary>
        /// The offsets from the camera that <see cref="HoldInteriorsAroundCamera"/> looks for interiors at,
        /// nearest first so that a pass cut short has still covered everything close by.
        ///
        /// Two cylinders of points, one fine and one coarse, both taller than a building is high — an interior
        /// is only ever found by asking about a point inside it, so anything the grid steps over is an interior
        /// that is not found. Built once, since it is the same relative grid wherever the camera happens to be.
        /// </summary>
        private static readonly Vector3[] InteriorScanOffsets = BuildInteriorScanOffsets();

        private static Vector3[] BuildInteriorScanOffsets()
        {
            var offsets = new List<Vector3>();

            AddInteriorScanPoints(offsets, InteriorNearRadius, InteriorNearHeight, InteriorNearStep, InteriorNearStepZ, 0f, 0f);
            AddInteriorScanPoints(offsets, InteriorFarRadius, InteriorFarHeight, InteriorFarStep, InteriorFarStepZ,
                InteriorNearRadius, InteriorNearHeight);

            offsets.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
            return offsets.ToArray();
        }

        /// <summary>
        /// Fills a cylinder of <paramref name="radius"/> and half-height <paramref name="height"/> around the
        /// origin with points <paramref name="step"/> apart, leaving out the ones already covered by a finer
        /// cylinder of <paramref name="skipRadius"/> and <paramref name="skipHeight"/>.
        /// </summary>
        private static void AddInteriorScanPoints(List<Vector3> offsets, float radius, float height, float step,
            float stepZ, float skipRadius, float skipHeight)
        {
            // Counted out in whole steps from the middle rather than walked from one edge to the other, so that
            // the grid is centred on the camera however the radius and the step divide.
            var acrossSteps = (int)(radius / step);
            var upSteps = (int)(height / stepZ);

            for (var ix = -acrossSteps; ix <= acrossSteps; ix++)
            for (var iy = -acrossSteps; iy <= acrossSteps; iy++)
            {
                var x = ix * step;
                var y = iy * step;

                // A circle rather than the square the two loops walk: the corners of the square lie half again
                // as far out as the radius asked for, for a third more points and no more building.
                var distanceSq = x * x + y * y;
                if (distanceSq > radius * radius) continue;

                var skipColumn = distanceSq <= skipRadius * skipRadius;

                for (var iz = -upSteps; iz <= upSteps; iz++)
                {
                    var z = iz * stepZ;
                    if (skipColumn && Math.Abs(z) <= skipHeight) continue;

                    offsets.Add(new Vector3(x, y, z));
                }
            }
        }

        /// <summary>
        /// Keeps every interior around the freecam loaded and drawn, for as long as the freecam is up.
        ///
        /// The game works out which interior to stream and to draw from where the player is, and the player is
        /// not where the camera is: freelook parks them eight metres behind it (see
        /// <see cref="ProcessFreelook"/>) and drags them along frozen. Take the camera out through a wall — or
        /// merely up to one, since eight metres is thicker than most of them — and the player is carried out of
        /// the interior's bounds with it, which the game reads as having left: the interior is deactivated and
        /// streamed out, and from the inside the room is an empty shell. Flying back in does not bring it back
        /// either, because the player is being teleported rather than walked in through a door, and it is the
        /// walking in that would otherwise ask for it again.
        ///
        /// Holding only the one interior the camera is standing in is not enough, because what looks like one
        /// building is not one interior: a tower is a stack of them, one per floor, side rooms and stairwells
        /// beside them, and the game hands out exactly one at a time. Flying up through the ceiling would then
        /// mean giving up the floor below to get the floor above, and the shell of the building is all that is
        /// left of everywhere the camera is not — which is the thing this is here to stop.
        ///
        /// So the whole neighbourhood is asked for instead. <see cref="InteriorScanOffsets"/> is walked as a
        /// grid of points around the camera, above and below it as much as around it, and every interior found
        /// at any of them is pinned in memory — which is what stops the streamer from dropping it — and set
        /// active on every frame, which is what undoes the player being dragged out of it. Nothing is given up
        /// on the way back out into the world: what is found is held until the freecam goes down (bar
        /// <see cref="ForgetDistantInteriors"/>, which only lets go of what the camera has left far behind), so
        /// a building worked in and out of over and over is still there on every way back in.
        ///
        /// The grid is far too large to walk in one frame, so a pass is spread over as many frames as it takes
        /// at <see cref="InteriorScanSamplesPerTick"/> points each — a fraction of a second — and offsets are
        /// ordered nearest first so that wherever the camera itself is comes back on the first frame of it.
        /// </summary>
        private void HoldInteriorsAroundCamera()
        {
            var cameraPos = _mainCamera.Position;

            // A pass starts over from where the camera is now once the last one has run out of points, or once
            // the camera has flown far enough that the pass in progress is being run around a spot it has long
            // since left. Nothing is lost by cutting one short: the offsets it had left are the far ones, and
            // the near ones it had already covered are the ones that matter.
            if (_interiorScanCursor >= InteriorScanOffsets.Length ||
                (cameraPos - _interiorScanOrigin).Length() > InteriorRescanDistance)
            {
                ForgetDistantInteriors(cameraPos);
                _interiorScanOrigin = cameraPos;
                _interiorScanCursor = 0;
            }

            var passEnd = Math.Min(_interiorScanCursor + InteriorScanSamplesPerTick, InteriorScanOffsets.Length);
            for (; _interiorScanCursor < passEnd; _interiorScanCursor++)
            {
                var at = _interiorScanOrigin + InteriorScanOffsets[_interiorScanCursor];

                // Answered from the map data rather than from what is in memory, so the answer does not depend
                // on the interior being loaded at the time of asking. Which is the whole point of scanning
                // ahead: nearly everything this finds is not loaded yet, and is being asked for so that it is.
                var interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS, at.X, at.Y, at.Z);
                if (interior == 0 || _heldInteriors.ContainsKey(interior)) continue;

                Function.Call(Hash.PIN_INTERIOR_IN_MEMORY, interior);
                _heldInteriors.Add(interior, at);
            }

            // Every frame and for every one of them, because the player being dragged around eight metres
            // behind the camera keeps wandering out through the walls of whichever interior they were last in,
            // and the game reads each of those as having left and switches that interior off.
            foreach (var interior in _heldInteriors.Keys)
                Function.Call(Hash.SET_INTERIOR_ACTIVE, interior, true);
        }

        /// <summary>
        /// Lets go of the interiors the camera has left more than <see cref="InteriorForgetDistance"/> behind.
        ///
        /// Held interiors are held whole — geometry, collision and props — and a long session flying across the
        /// map would otherwise go on stacking up every building passed through, in memory, until the freecam
        /// went down. That distance is far outside anything a camera can see out of, and far outside the scan
        /// radius, so nothing is dropped that flying back would not simply find and pin again within a pass.
        /// </summary>
        private void ForgetDistantInteriors(Vector3 cameraPos)
        {
            List<int> distant = null;

            foreach (var held in _heldInteriors)
            {
                if ((held.Value - cameraPos).Length() <= InteriorForgetDistance) continue;
                (distant ?? (distant = new List<int>())).Add(held.Key);
            }

            if (distant == null) return;

            foreach (var interior in distant)
            {
                Function.Call(Hash.UNPIN_INTERIOR, interior);
                _heldInteriors.Remove(interior);
            }
        }

        /// <summary>
        /// Hands everything <see cref="HoldInteriorsAroundCamera"/> is holding back to the game, which is then
        /// free to stream each of them out again once nothing is left inside it. Safe to call on any tick: it
        /// does nothing unless there are interiors being held.
        /// </summary>
        private void ReleaseHeldInteriors()
        {
            if (_heldInteriors.Count == 0) return;

            foreach (var interior in _heldInteriors.Keys)
                Function.Call(Hash.UNPIN_INTERIOR, interior);

            _heldInteriors.Clear();

            // The next freecam session starts its own pass, around wherever it goes up, rather than carrying on
            // through the leftovers of this one.
            _interiorScanCursor = 0;
            _interiorScanOrigin = Vector3.Zero;
        }

        /// <summary>
        /// The height of the first solid thing under <paramref name="from"/>, or null when the probe comes
        /// back with nothing.
        ///
        /// The probe is the synchronous kind on purpose. The ordinary one is a request the game answers a
        /// frame or more later, which is fine for the placement raycasts that run on every tick and useless
        /// here: this is asked once, and an answer of "not ready yet" would read as "nothing below" and leave
        /// the player hanging in the air at random.
        /// </summary>
        private static float? GroundBelow(Vector3 from, Entity toIgnore)
        {
            const float probeLength = 1000f;

            var probe = ShapeTest.StartExpensiveSyncTestLOSProbe(from, from - new Vector3(0f, 0f, probeLength),
                IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Vehicles, toIgnore,
                ShapeTestOptions.Default);

            ShapeTestResult result;
            if (probe.GetResult(out result) != ShapeTestStatus.Ready || !result.DidHit) return null;

            return result.HitPosition.Z;
        }

        private void NewMap()
        {
            ClearMultiSelection();
            JavascriptHook.StopAllScripts();
            PropStreamer.RemoveAll();
            PropStreamer.Markers.Clear();
            _currentObjectsMenu.Clear();
            PropStreamer.Identifications.Clear();
            PropStreamer.ActiveScenarios.Clear();
            PropStreamer.ActiveRelationships.Clear();
            PropStreamer.ActiveWeapons.Clear();
            PropStreamer.Doors.Clear();
            PropStreamer.CurrentMapMetadata = new MapMetadata();
            ModManager.CurrentMod?.ModDisconnectInvoker();
            ModManager.CurrentMod = null;
            foreach (MapObject o in PropStreamer.RemovedObjects)
            {
                var t = World.CreateProp(o.Hash, o.Position, o.Rotation, true, false);
                t.PositionNoOffset = o.Position;
            }
            PropStreamer.RemovedObjects.Clear();
            _loadedEntities = 0;
            _changesMade = 0;
            _lastAutosave = DateTime.Now;
            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Loaded new map."));
        }

        /// <summary>
        /// Autoloaded maps are not part of the map being edited, so "New Map" leaves them standing: this is the
        /// only thing that takes them away.
        ///
        /// With several maps loaded the player gets to say which one goes; with only one there is nothing to
        /// choose between, so it goes on the spot rather than behind a menu holding a single row.
        /// </summary>
        private void UnloadAutoloadedMaps()
        {
            if (!AutoloadedMaps.Any) return;

            if (AutoloadedMaps.MapCount == 1)
            {
                UnloadAutoloadedMap(0);
                return;
            }

            RedrawUnloadAutoloadedMenu();
            SetMenuVisible(_mainMenu, false);
            SetMenuVisible(_unloadAutoloadedMenu, true);
        }

        private void UnloadAutoloadedMap(int index)
        {
            var name = AutoloadedMaps.Unload(index);
            if (name == null) return;

            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Unloaded autoloaded map:") + " ~h~" + name + "~h~.");
        }

        private void UnloadAllAutoloadedMaps()
        {
            if (!AutoloadedMaps.Any) return;

            var count = AutoloadedMaps.MapCount;
            AutoloadedMaps.UnloadAll();
            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Unloaded autoloaded maps:") + " ~h~" + count + "~h~.");
        }

        private void BeginSaveMap()
        {
            if (ModManager.CurrentMod != null)
            {
                string filename = Compat.GetUserInput(255);
                if (String.IsNullOrWhiteSpace(filename))
                {
                    Compat.Notify("~r~~h~Map Editor~h~~n~~w~" + Translation.Translate("The filename was empty."));
                    return;
                }
                Map tmpMap = new Map();
                tmpMap.Objects.AddRange(PropStreamer.GetAllEntities());
                tmpMap.RemoveFromWorld.AddRange(PropStreamer.RemovedObjects);
                tmpMap.Markers.AddRange(PropStreamer.Markers);
                Compat.Notify("~b~~h~Map Editor~h~~n~~w~" + Translation.Translate("Map sent to external mod for saving."));
                ModManager.CurrentMod.MapSavedInvoker(tmpMap, filename);
                return;
            }
            _savingMap = true;
            SetMenuVisible(_mainMenu, false);
            RedrawFormatMenu();
            SetMenuVisible(_formatMenu, true);
        }

        private void BeginLoadMap()
        {
            _savingMap = false;
            SetMenuVisible(_mainMenu, false);
            RedrawFormatMenu();
            SetMenuVisible(_formatMenu, true);
        }

        private void OnFormatSelect(object sender, ItemActivatedArgs e)
        {
            int indx = _formatMenu.Items.IndexOf(e.Item);

            if (_savingMap)
            {
                string filename = "";
                if (indx != 0)
                    filename = Compat.GetUserInput(255);

                switch (indx)
                {
                    case 0: // XML
                        SetMenuVisible(_formatMenu, false);
                        RedrawMetadataMenu();
                        SetMenuVisible(_metadataMenu, true);
                        return;
                    case 1: // Objects.ini
                        if (!filename.EndsWith(".ini")) filename += ".ini";
                        SaveMap(filename, MapSerializer.Format.SimpleTrainer);
                        break;
                    case 2: // C#
                        if (!filename.EndsWith(".cs")) filename += ".cs";
                        SaveMap(filename, MapSerializer.Format.CSharpCode);
                        break;
                    case 3: // Raw
                        if (!filename.EndsWith(".txt")) filename += ".txt";
                        SaveMap(filename, MapSerializer.Format.Raw);
                        break;
                    case 4: // SpoonerLegacy
                        if (!filename.EndsWith(".SP00N")) filename += ".SP00N";
                        SaveMap(filename, MapSerializer.Format.SpoonerLegacy);
                        break;
                    case 5: // Menyoo
                        if (!filename.EndsWith(".xml")) filename += ".xml";
                        SaveMap(filename, MapSerializer.Format.Menyoo);
                        break;
                }
            }
            else
            {
                string filename = "";
                if (indx != 4)
                    filename = Compat.GetUserInput(255);

                MapSerializer.Format tmpFor;
                switch (indx)
                {
                    case 0:
                        tmpFor = MapSerializer.Format.NormalXml;
                        break;
                    case 1:
                        tmpFor = MapSerializer.Format.SimpleTrainer;
                        break;
                    case 2:
                        tmpFor = MapSerializer.Format.SpoonerLegacy;
                        break;
                    case 3:
                        tmpFor = MapSerializer.Format.Menyoo;
                        break;
                    case 4: // File picker
                        SetMenuVisible(_formatMenu, false);
                        RedrawFilepickerMenu();
                        SetMenuVisible(_filepicker, true);
                        return;
                    default:
                        return;
                }
                LoadMap(filename, tmpFor);
            }
            SetMenuVisible(_formatMenu, false);
        }

	    private void LoadSettings()
	    {
		    if (File.Exists("scripts\\MapEditor.xml"))
		    {
			    XmlSerializer serializer = new XmlSerializer(typeof(Settings));
			    var file = new StreamReader("scripts\\MapEditor.xml");
			    _settings = (Settings) serializer.Deserialize(file);
				file.Close();
			    if (_settings.ActivationKey == Keys.None)
			    {
				    _settings.ActivationKey = Keys.F7;
					SaveSettings();
			    }

		        if (_settings.GamepadCameraSensitivity == 0)
		            _settings.GamepadCameraSensitivity = 5;
                if (_settings.KeyboardMovementSensitivity == 0)
                    _settings.KeyboardMovementSensitivity = 30;
                if (_settings.GamepadMovementSensitivity == 0)
                    _settings.GamepadMovementSensitivity = 30;
		        if (_settings.AutosaveInterval == 0)
		            _settings.AutosaveInterval = 5;
		        if (_settings.DrawDistance == 0)
		            _settings.DrawDistance = -1;
		        if (!_settings.BoundingBox.HasValue)
		            _settings.BoundingBox = true;
		        // Zero is what a settings file written before there was a streaming range deserializes to, and
		        // it is not a range anyone can have picked: switching it off is SmartStreaming.Off.
		        if (_settings.StreamingRange == 0)
		            _settings.StreamingRange = SmartStreaming.DefaultRange;
		    }
		    else
		    {
		        _settings = new Settings();
				SaveSettings();
		    }

		    ObjectDatabase.TrackInvalidObjects = _settings.OmitInvalidObjects;
		    SmartStreaming.Range = _settings.StreamingRange;
	    }

	    private void SaveSettings()
	    {
		    XmlSerializer serializer = new XmlSerializer(typeof(Settings));
		    var file = new StreamWriter("scripts\\MapEditor.xml");
			serializer.Serialize(file, _settings);
			file.Close();
	    }

		/// <summary>The extension a format is saved and looked for under.</summary>
		private static string ExtensionFor(MapSerializer.Format format)
		{
			switch (format)
			{
				case MapSerializer.Format.SimpleTrainer: return ".ini";
				case MapSerializer.Format.SpoonerLegacy: return ".SP00N";
				case MapSerializer.Format.CSharpCode: return ".cs";
				case MapSerializer.Format.Raw: return ".txt";
				default: return ".xml";
			}
		}

		/// <summary>
		/// Finds the map behind the name the player typed. Saved maps live in <see cref="UserMaps.Folder"/>, so a
		/// bare name is looked for there as well as beside the game, and the format's extension is filled in when
		/// they left it off. Null when nothing answers to that name.
		/// </summary>
		private static string ResolveMapPath(string filename, MapSerializer.Format format)
		{
			if (string.IsNullOrWhiteSpace(filename)) return null;

			filename = filename.Trim();

			var names = new List<string> { filename };

			var extension = ExtensionFor(format);
			if (!filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				names.Add(filename + extension);

			foreach (var name in names)
			{
				if (File.Exists(name)) return name;

				var inUserMaps = Path.Combine(UserMaps.Folder, name);
				if (File.Exists(inUserMaps)) return inUserMaps;
			}

			return null;
		}

		/// <summary>
		/// Picks the map back up where the instance before this one left it, if the script was reloaded with a
		/// map open. It comes back as the map being edited — in the entity menu, in the counter, selectable,
		/// saveable, and cleared by "New Map" — which is the whole point of the round trip: the objects were
		/// standing in the world either way, and this is what makes them the player's again.
		/// </summary>
		private void RestoreSession()
		{
			if (!SessionRestore.Pending) return;

			LoadMap(SessionRestore.FilePath, MapSerializer.Format.NormalXml, restoring: true);

			// Taken away as soon as it has been spent. It is a hand-off between two instances of the script, not
			// a map file, and it must not be restored a second time on some later start.
			SessionRestore.Discard();

			Compat.Notify("~b~~h~Map Editor~h~~w~~n~" +
			              Translation.Translate("Restored the map you were working on before the script reloaded."));
		}

		/// <summary>
		/// <paramref name="restoring"/> marks the map as one the editor is picking back up after a script reload
		/// rather than one the player just opened: it was never really unloaded, so it keeps the metadata it
		/// already had — the file it is being saved to above all — and it stays where it is instead of dragging
		/// the player back to its loading point.
		/// </summary>
	    private void LoadMap(string filename, MapSerializer.Format format, bool restoring = false)
	    {
		    filename = ResolveMapPath(filename, format);

			if (filename == null)
			{
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("The filename was empty or the file does not exist!"));
				return;
			}
            var handles = new List<int>();
			var des = new MapSerializer();
		    try
		    {
			    var map2Load = des.Deserialize(filename, format);
			    if (map2Load == null) return;

		        if (!restoring && map2Load.Metadata != null && map2Load.Metadata.LoadingPoint.HasValue)
		        {
		            Game.Player.Character.Position = map2Load.Metadata.LoadingPoint.Value;
                    Wait(500);

                    // The player is somewhere else now, and what of the map is worth putting into the world is
                    // measured from where they are, not from where they were when the frame started.
                    SmartStreaming.BeginTick(CurrentStreamingOrigin);
		        }

			    foreach (MapObject o in map2Load.Objects)
			    {
				    if(o == null) continue;
			        _loadedEntities++;

                    if (o.Type == ObjectTypes.Pickup)
                    {
                        var newPickup = PropStreamer.CreatePickup(o.Hash, o.Position, o.Rotation.Z, o.Amount, o.Dynamic, o.Quaternion);
                        newPickup.Timeout = o.RespawnTimer;
                        AddItemToEntityMenu(newPickup);
                        if (o.Id != null && !PropStreamer.Identifications.ContainsKey(newPickup.ObjectHandle))
                        {
                            PropStreamer.Identifications.Add(newPickup.ObjectHandle, o.Id);
                            handles.Add(newPickup.ObjectHandle);
                        }
                        continue;
                    }

                    // The far side of a large map is loaded without ever being put into the world: it is in the
                    // map, in the entity menu and in what gets saved, and streaming spawns it if and when the
                    // player goes there. An object with a name is spawned wherever it is, because the map's
                    // script is about to be handed it and cannot be handed one that is not there.
                    if (o.Id == null && !SmartStreaming.HasReturned(o.Position))
                    {
                        AddStreamedItemToEntityMenu(o, PropStreamer.AddStreamedOut(o).Uid);
                        continue;
                    }

                    // The same spawn smart streaming uses to put an entity back after the player has been away
                    // from it, so that a map that has been flown out of and back into is still the map that was
                    // loaded. See <see cref="SpawnMapObject"/>.
                    var entity = SpawnMapObject(o);
                    if (entity == null) continue;

                    AddItemToEntityMenu(entity);
                    if (o.Id != null) handles.Add(entity.Handle);
			    }
			    foreach (MapObject o in map2Load.RemoveFromWorld)
			    {
					if(o == null) continue;
				    PropStreamer.RemovedObjects.Add(o);
				    Prop returnedProp = Function.Call<Prop>(Hash.GET_CLOSEST_OBJECT_OF_TYPE, o.Position.X, o.Position.Y,
					    o.Position.Z, 1f, o.Hash, 0);
				    if (returnedProp == null || returnedProp.Handle == 0) continue;
                    MapObject tmpObj = new MapObject()
                    {
                        Hash = returnedProp.Model.Hash,
                        Position = returnedProp.Position,
                        Rotation = returnedProp.Rotation,
                        Quaternion = Quaternion.GetEntityQuaternion(returnedProp),
                        Type = ObjectTypes.Prop,
                        Id = _mapObjCounter.ToString(),
                    };
                    _mapObjCounter++;
                    AddItemToEntityMenu(tmpObj);
                    returnedProp.Delete();
			    }
			    foreach (Marker marker in map2Load.Markers)
			    {
				    if(marker == null) continue;
			        _markerCounter++;
			        marker.Id = _markerCounter;
					PropStreamer.Markers.Add(marker);
					AddItemToEntityMenu(marker);
			    }

		        if (_settings.LoadScripts && format == MapSerializer.Format.NormalXml &&
		            File.Exists(new FileInfo(filename).Directory.FullName + "\\" + Path.GetFileNameWithoutExtension(filename) + ".js"))
		        {
                    JavascriptHook.StartScript(File.ReadAllText(new FileInfo(filename).Directory.FullName + "\\" + Path.GetFileNameWithoutExtension(filename) + ".js"), handles);
		        }

		        if (!restoring && map2Load.Metadata != null && map2Load.Metadata.TeleportPoint.HasValue)
		        {
		            Game.Player.Character.Position = map2Load.Metadata.TeleportPoint.Value;
		        }

		        PropStreamer.CurrentMapMetadata = map2Load.Metadata ?? new MapMetadata();

		        // A restored map is still the map it was before the reload, saved to the file it was already
		        // saved to. The session file it came back from is not that file, and must not become it.
		        if (!restoring)
		            PropStreamer.CurrentMapMetadata.Filename = filename;

		        // The caller says what a restore has to say, and it is not "loaded map scripts\MapEditor.SessionRestore.xml".
		        if (!restoring)
			        Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Loaded map") + " ~h~" + filename + "~h~.");
		    }
		    catch (Exception e)
		    {
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to load, see error below."));
				Compat.Notify(e.Message);

                File.AppendAllText("scripts\\MapEditor.log", DateTime.Now + " MAP FAILED TO LOAD:\r\n" + e.ToString() + "\r\n");
			}
	    }

	    private void SaveMap(string filename, MapSerializer.Format format)
	    {
			if (String.IsNullOrWhiteSpace(filename))
			{
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("The filename was empty!"));
				return;
			}

			var ser = new MapSerializer();
			var tmpmap = new Map();
			try
			{
				// A bare filename is relative to the game's own directory, which is where maps used to pile up.
				var path = UserMaps.Resolve(filename);

				tmpmap.Objects.AddRange(format == MapSerializer.Format.SimpleTrainer
					? PropStreamer.GetAllEntities().Where(p => p.Type == ObjectTypes.Prop)
					: PropStreamer.GetAllEntities());
				tmpmap.RemoveFromWorld.AddRange(PropStreamer.RemovedObjects);
				tmpmap.Markers.AddRange(PropStreamer.Markers);
			    tmpmap.Metadata = PropStreamer.CurrentMapMetadata;

				ser.Serialize(path, tmpmap, format);
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Saved current map as") + " ~h~" + path + "~h~.");
			    _changesMade = 0;
			}
			catch (Exception e)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to save, see error below."));
				Compat.Notify(e.Message);
			}
		}

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _settings.ActivationKey && !_menuPool.AreAnyVisible)
            {
                _mainMenu.Visible = !_mainMenu.Visible;
            }
        }

        public static float GetSafeFloat(string input, float lastFloat)
        {
            float output;
            if (!float.TryParse(input, NumberStyles.Any,  CultureInfo.InvariantCulture, out output))
            {
                return lastFloat;
            }

            if (output < -(_possibleRange*0.01f) || output > (_possibleRange*0.01f))
            {
                return lastFloat;
            }
            return output;
        }

        public static bool IsPed(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_PED, ent);
        }

        public static bool IsVehicle(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_VEHICLE, ent);
        }

        public static bool IsProp(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, ent);
        }

        public void ValidateDatabase()
	    {
		    // Validate object list.
		    Dictionary<string, int> tmpDict = new Dictionary<string, int>();
		    int counter = 0;
		    while (counter < ObjectDatabase.MainDb.Count)
		    {
			    var pair = ObjectDatabase.MainDb.ElementAt(counter);
			    counter++;
		        Screen.ShowSubtitle((counter) + "/" + ObjectDatabase.MainDb.Count + " done. (" +
		            (counter/(float) ObjectDatabase.MainDb.Count)*100 +
		            "%)\nValid objects: " + tmpDict.Count, 2000);
                Yield();

		        var model = new Model(pair.Value);
		        model.Request(100);
		        if (!model.IsLoaded)
		        {
                    model.MarkAsNoLongerNeeded();
		            continue;
		        }
                model.MarkAsNoLongerNeeded();
                if (!tmpDict.ContainsKey(pair.Key))
				    tmpDict.Add(pair.Key, pair.Value);
		    }
			string output = tmpDict.Aggregate("", (current, pair) => current + (pair.Key + "=" + pair.Value + "\r\n"));
		    File.WriteAllText("scripts\\ObjectList.ini", output);
	    }
	}
}
