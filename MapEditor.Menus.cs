using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI.Menus;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// Badges replacing NativeUI's BadgeStyle.Franklin / BadgeStyle.Alert.
        /// </summary>
        private static readonly BadgeSet FolderBadge = new BadgeSet("commonmenu", "shop_franklin_icon_a", "shop_franklin_icon_b");
        private static readonly BadgeSet AlertBadge = new BadgeSet("commonmenu", "mp_alerttriangle", "mp_alerttriangle");

        /// <summary>Marks a starred model, wherever it is listed. See <see cref="ToggleFavorite"/>.</summary>
        private static readonly BadgeSet StarBadge = new BadgeSet("commonmenu", "shop_new_star", "shop_new_star");

        private enum EntityMenuKind
        {
            Entity,
            World,
            Marker,
            Pickup,

            /// <summary>
            /// An entity of the map that is not in the world at the moment, because the player is nowhere near
            /// it. Its row is the same row it had while it was standing, pointed at the record smart streaming
            /// is holding it in rather than at a handle the game has taken back.
            /// </summary>
            Streamed,
        }

        /// <summary>
        /// Identifies what a "Current Entities" row points at. The old code smuggled this
        /// through the item Description, which LemonUI renders to the player.
        /// </summary>
        private class EntityMenuTag
        {
            public EntityMenuKind Kind;
            public int Id;          // entity handle, marker id or pickup UID
            public string WorldId;  // MapObject.Id, for props removed from the world
        }

        /// <summary>A menu's worth of rows, each paired with the model hash its "Invalid" flag is read from.</summary>
        private class RowGroup
        {
            public readonly List<NativeItem> Items = new List<NativeItem>();
            public readonly List<int> Hashes = new List<int>();
        }

        /// <summary>The rows of one category, grouped the way the DLC filter reads them.</summary>
        private class CategoryRows
        {
            /// <summary>
            /// Rows per DLC name, always including <see cref="Dlc.AllName"/>, which holds the whole category.
            /// A filter change is then a lookup, not a rebuild.
            /// </summary>
            public readonly Dictionary<string, RowGroup> ByDlc = new Dictionary<string, RowGroup>();

            /// <summary>The filter's values in release order, or empty for a list that offers no filter.</summary>
            public readonly List<string> Dlcs = new List<string>();
        }

        /// <summary>Every row built so far, per object type, keyed by model name. See <see cref="RowFor"/>.</summary>
        private readonly Dictionary<ObjectTypes, Dictionary<string, NativeItem>> _rowsByModel =
            new Dictionary<ObjectTypes, Dictionary<string, NativeItem>>();

        /// <summary>The prepared rows of every category opened so far. See <see cref="RowsFor"/>.</summary>
        private readonly Dictionary<ObjectCategory, CategoryRows> _rowsByCategory =
            new Dictionary<ObjectCategory, CategoryRows>();

        /// <summary>
        /// The models whose rows are still to be built ahead of the player, and how far along that is. Null
        /// once there is nothing left to build. See <see cref="WarmObjectRows"/>.
        /// </summary>
        private List<string> _rowWarmupQueue;
        private int _rowWarmupIndex;
        private bool _rowWarmupStarted;

        /// <summary>
        /// How long one tick may spend building rows ahead of the player. Building all 25,000 prop rows at
        /// once would just move the stall to whenever that happened, so it is spread over frames instead: at
        /// this budget the whole list is ready within a few seconds of opening the menu, well before the
        /// player has finished picking a category, and no single frame is late.
        /// </summary>
        private const long RowWarmupBudgetMs = 2;

        /// <summary>
        /// Starts building the prop rows in the background, on the first sign the player is heading for the
        /// object list. Does nothing once they are built, so it is safe to call on every menu opening.
        /// </summary>
        private void BeginObjectRowWarmup()
        {
            if (_rowWarmupStarted) return;
            _rowWarmupStarted = true;

            // A snapshot, so that a model folded into the database later cannot invalidate the walk. Rows the
            // player got to first are already in _rowsByModel and are skipped when the queue reaches them.
            _rowWarmupQueue = new List<string>(ObjectDatabase.MainDb.Keys);
            _rowWarmupIndex = 0;
        }

        /// <summary>
        /// Builds the next slice of the prop rows. Whatever is left when the player reaches a category is
        /// built on the spot by <see cref="RowsFor"/>, so this only ever has to be fast, never complete.
        /// </summary>
        private void WarmObjectRows()
        {
            if (_rowWarmupQueue == null) return;

            var clock = Stopwatch.StartNew();
            while (_rowWarmupIndex < _rowWarmupQueue.Count)
            {
                RowFor(ObjectTypes.Prop, _rowWarmupQueue[_rowWarmupIndex++]);
                if (clock.ElapsedMilliseconds >= RowWarmupBudgetMs) return;
            }

            _rowWarmupQueue = null;
        }

        private void BuildSettingsMenu()
        {
            var possibleLangauges = new List<string> { "Auto" };
            possibleLangauges.AddRange(Translation.Translations.Select(t => t.Language).ToList());

            var language = new NativeListItem<string>(Translation.Translate("Language"), possibleLangauges.ToArray())
            {
                SelectedIndex = Math.Max(0, possibleLangauges.IndexOf(_settings.Translation)),
            };
            language.ItemChanged += (sender, e) =>
            {
                var newLanguage = e.Object;
                Translation.SetLanguage(newLanguage);
                _settings.Translation = newLanguage;
                SaveSettings();
                if (newLanguage == "Auto")
                {
                    language.Description = "Use your game's language settings.";
                    return;
                }
                var descFile = Translation.Translations.FirstOrDefault(t => t.Language == newLanguage);
                if (descFile == null) return;
                language.Description = "~h~" + Translation.Translate("Translator") + ":~h~ " + descFile.Translator;
            };

            var crosshairNames = Enum.GetNames(typeof(CrosshairType));
            var checkem = new NativeListItem<string>(Translation.Translate("Marker"), crosshairNames)
            {
                SelectedIndex = Math.Max(0, crosshairNames.ToList().FindIndex(x => x == _settings.CrosshairType.ToString())),
            };
            checkem.ItemChanged += (sender, e) =>
            {
                CrosshairType outHash;
                Enum.TryParse(e.Object, out outHash);
                _settings.CrosshairType = outHash;
                SaveSettings();
            };

            var disableText = Translation.Translate("Disable");
            var autosaveList = new List<string> { disableText };
            for (int i = 5; i <= 60; i += 5)
                autosaveList.Add(i.ToString(CultureInfo.InvariantCulture));

            int aIndex = autosaveList.IndexOf(_settings.AutosaveInterval.ToString(CultureInfo.InvariantCulture));
            if (aIndex == -1) aIndex = 0;

            var autosaveItem = new NativeListItem<string>(Translation.Translate("Autosave Interval"),
                Translation.Translate("Interval in minutes between automatic autosaves."), autosaveList.ToArray())
            {
                SelectedIndex = aIndex,
            };
            autosaveItem.ItemChanged += (sender, e) =>
            {
                _settings.AutosaveInterval = e.Object == disableText ? -1 : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SaveSettings();
            };

            var defaultText = Translation.Translate("Default");
            var possibleDrawDistances = new List<string> { defaultText, "50", "75" };
            for (int i = 100; i <= 3000; i += 100)
                possibleDrawDistances.Add(i.ToString(CultureInfo.InvariantCulture));

            int dIndex = possibleDrawDistances.IndexOf(_settings.DrawDistance.ToString(CultureInfo.InvariantCulture));
            if (dIndex == -1) dIndex = 0;

            var drawDistanceItem = new NativeListItem<string>(Translation.Translate("Draw Distance"),
                Translation.Translate("Draw distance for props, vehicles and peds. Reload the map for changes to take effect."),
                possibleDrawDistances.ToArray())
            {
                SelectedIndex = dIndex,
            };
            drawDistanceItem.ItemChanged += (sender, e) =>
            {
                _settings.DrawDistance = e.Object == defaultText ? -1 : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SaveSettings();
            };

            var streamingRanges = new List<string> { disableText };
            for (int i = 100; i <= 1000; i += 100)
                streamingRanges.Add(i.ToString(CultureInfo.InvariantCulture));
            streamingRanges.Add("1500");
            streamingRanges.Add("2000");
            streamingRanges.Add("3000");

            int sIndex = streamingRanges.IndexOf(_settings.StreamingRange.ToString(CultureInfo.InvariantCulture));
            if (sIndex == -1) sIndex = streamingRanges.IndexOf(SmartStreaming.DefaultRange.ToString(CultureInfo.InvariantCulture));

            var streamingItem = new NativeListItem<string>(Translation.Translate("Streaming Range"),
                Translation.Translate("How far away a placed prop, vehicle or ped is unloaded from the world, in metres." +
                                      " It comes back when you return. Applies to the map you are building and to autoloaded maps."),
                streamingRanges.ToArray())
            {
                SelectedIndex = ClampIndex(sIndex, streamingRanges.Count),
            };
            streamingItem.ItemChanged += (sender, e) =>
            {
                _settings.StreamingRange = e.Object == disableText
                    ? SmartStreaming.Off
                    : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SmartStreaming.Range = _settings.StreamingRange;
                SaveSettings();
            };

            var senslist = Enumerable.Range(1, 59).ToArray();

            var gamboy = new NativeListItem<int>(Translation.Translate("Mouse Camera Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.CameraSensivity - 1, senslist.Length),
            };
            gamboy.ItemChanged += (sender, e) =>
            {
                _settings.CameraSensivity = e.Object;
                SaveSettings();
            };

            var gampadSens = new NativeListItem<int>(Translation.Translate("Gamepad Camera Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.GamepadCameraSensitivity - 1, senslist.Length),
            };
            gampadSens.ItemChanged += (sender, e) =>
            {
                _settings.GamepadCameraSensitivity = e.Object;
                SaveSettings();
            };

            var keymovesens = new NativeListItem<int>(Translation.Translate("Keyboard Movement Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.KeyboardMovementSensitivity - 1, senslist.Length),
            };
            keymovesens.ItemChanged += (sender, e) =>
            {
                _settings.KeyboardMovementSensitivity = e.Object;
                SaveSettings();
            };

            var gammovesens = new NativeListItem<int>(Translation.Translate("Gamepad Movement Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.GamepadMovementSensitivity - 1, senslist.Length),
            };
            gammovesens.ItemChanged += (sender, e) =>
            {
                _settings.GamepadMovementSensitivity = e.Object;
                SaveSettings();
            };

            var butts = new NativeCheckboxItem(Translation.Translate("Instructional Buttons"), _settings.InstructionalButtons);
            butts.CheckboxChanged += (sender, e) =>
            {
                _settings.InstructionalButtons = butts.Checked;
                SaveSettings();
            };

            var gamepadItem = new NativeCheckboxItem(Translation.Translate("Enable Gamepad Shortcut"), _settings.Gamepad);
            gamepadItem.CheckboxChanged += (sender, e) =>
            {
                _settings.Gamepad = gamepadItem.Checked;
                SaveSettings();
            };

            var counterItem = new NativeCheckboxItem(Translation.Translate("Entity Counter"), _settings.PropCounterDisplay);
            counterItem.CheckboxChanged += (sender, e) =>
            {
                _settings.PropCounterDisplay = counterItem.Checked;
                SaveSettings();
            };

            var snapper = new NativeCheckboxItem(Translation.Translate("Follow Object With Camera"), _settings.SnapCameraToSelectedObject);
            snapper.CheckboxChanged += (sender, e) =>
            {
                _settings.SnapCameraToSelectedObject = snapper.Checked;
                SaveSettings();
            };

            var boundItem = new NativeCheckboxItem(Translation.Translate("Bounding Box"), _settings.BoundingBox.GetValueOrDefault(false));
            boundItem.CheckboxChanged += (sender, e) =>
            {
                _settings.BoundingBox = boundItem.Checked;
                SaveSettings();
            };

            var worldNamesItem = new NativeCheckboxItem(Translation.Translate("World Object Names"), Translation.Translate(
                "Shows the model name of the game's own objects around you, so you can copy or favorite them by sight."),
                _settings.WorldObjectNames);
            worldNamesItem.CheckboxChanged += (sender, e) =>
            {
                _settings.WorldObjectNames = worldNamesItem.Checked;
                SaveSettings();
            };

            var scriptItem = new NativeCheckboxItem(Translation.Translate("Execute Scripts"), _settings.LoadScripts);
            scriptItem.CheckboxChanged += (sender, e) =>
            {
                _settings.LoadScripts = scriptItem.Checked;
                SaveSettings();
            };

            var objectValidationItem = new NativeCheckboxItem(Translation.Translate("Skip Invalid Objects"), _settings.OmitInvalidObjects);
            objectValidationItem.CheckboxChanged += (sender, e) =>
            {
                _settings.OmitInvalidObjects = objectValidationItem.Checked;
                ObjectDatabase.TrackInvalidObjects = objectValidationItem.Checked;
                SaveSettings();
                RedrawObjectsMenu(_currentCategory, _currentObjectType);
            };

            var resetInvalid = new NativeItem(Translation.Translate("Reset Invalid Objects"), Translation.Translate(
                "Forgets every object marked as invalid, so they are checked against the game again next time you browse them."));
            resetInvalid.Activated += (men, item) =>
            {
                ObjectDatabase.ClearInvalidHashes();
                RedrawObjectsMenu(_currentCategory, _currentObjectType);
            };

            var regenCategories = new NativeItem(Translation.Translate("Rebuild Vehicle & Ped Categories"), Translation.Translate(
                "Rewrites the generated vehicle and ped category files from scratch, discarding any edits made to them." +
                " Prop categories are not touched."));
            regenCategories.Activated += (men, item) =>
            {
                ObjectCategories.Regenerate();
                // The categories are rebuilt from scratch, so the rows grouped under the old ones are stale.
                // The rows themselves are keyed by model and outlive this.
                _rowsByCategory.Clear();
                _currentCategory = null;
                _dlcFilterItem = null;
                _objectsMenu.Clear();
                RedrawCategoriesMenu(_currentObjectType);
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Categories rebuilt."));
            };

            var validate = new NativeItem(Translation.Translate("Validate Object Database"), Translation.Translate(
                "This will update the current object database, removing any invalid objects. The changes will take effect after you restart the script." +
                " It will take a couple of minutes."));
            validate.Activated += (men, item) => ValidateDatabase();

            var resetGrps = new NativeItem(Translation.Translate("Reset Active Relationship Groups"),
                Translation.Translate("This will set all ped's relationship groups to Companion."));
            resetGrps.Activated += (men, item) =>
            {
                PropStreamer.Peds.ForEach(handle =>
                {
                    var ped = Compat.PedFrom(handle);
                    if (ped != null) ObjectDatabase.SetPedRelationshipGroup(ped, "Companion");
                });
            };

#if DEBUG
            var testItem = new NativeItem("Load Terrain");
            testItem.Activated += (sender, item) =>
            {
                if (!Game.IsWaypointActive)
                {
                    Function.Call(Hash.CLEAR_HD_AREA);
                    return;
                }
                var wpyPos = World.WaypointPosition;
                Function.Call(Hash.SET_HD_AREA, wpyPos.X, wpyPos.Y, wpyPos.Z, 400f);
            };
            _settingsMenu.Add(testItem);
#endif

            _settingsMenu.Add(language);
            _settingsMenu.Add(gamepadItem);
            _settingsMenu.Add(drawDistanceItem);
            _settingsMenu.Add(streamingItem);
            _settingsMenu.Add(autosaveItem);
            _settingsMenu.Add(checkem);
            _settingsMenu.Add(boundItem);
            _settingsMenu.Add(gamboy);
            _settingsMenu.Add(gampadSens);
            _settingsMenu.Add(keymovesens);
            _settingsMenu.Add(gammovesens);
            _settingsMenu.Add(butts);
            _settingsMenu.Add(counterItem);
            _settingsMenu.Add(snapper);
            _settingsMenu.Add(worldNamesItem);
            _settingsMenu.Add(scriptItem);
            _settingsMenu.Add(objectValidationItem);
            _settingsMenu.Add(resetInvalid);
            _settingsMenu.Add(regenCategories);
            _settingsMenu.Add(validate);
            _settingsMenu.Add(resetGrps);
            _settingsMenu.SelectedIndex = 0;
            _settingsMenu.Buttons.Visible = false;
        }

        private static int ClampIndex(int index, int count)
        {
            if (index < 0) return 0;
            if (index >= count) return count - 1;
            return index;
        }

        private void OnIndexChange(object sender, SelectedEventArgs e)
        {
            var menu = (NativeMenu) sender;
            OnIndexChange(menu, e);
        }

        private void OnIndexChange(NativeMenu sender, SelectedEventArgs e)
        {
            // Rebuilding a menu moves its selection, which raises this event. Without this guard that would
            // spawn a preview prop while the player is somewhere else entirely, e.g. in the settings menu.
            if (!_isChoosingObject) return;

            int index = e.Index;
            if (index < 0 || index >= sender.Items.Count) return;

            // The DLC filter shares the object list, and there is no model behind it to preview.
            if (ReferenceEquals(sender.Items[index], _dlcFilterItem)) return;

            int requestedHash;
            var title = sender.Items[index].Title;
            switch (_currentObjectType)
            {
                case ObjectTypes.Prop:
                    if (!ObjectDatabase.MainDb.TryGetValue(title, out requestedHash)) return;
                    break;
                case ObjectTypes.Vehicle:
                    if (!ObjectDatabase.VehicleDb.TryGetValue(title, out requestedHash)) return;
                    break;
                case ObjectTypes.Ped:
                    if (!ObjectDatabase.PedDb.TryGetValue(title, out requestedHash)) return;
                    break;
                default:
                    return;
            }

            // A blacklisted model is re-checked rather than skipped: LoadObject rejects a genuinely missing
            // model in two native calls, so there is nothing to save by trusting a stale InvalidObjects.ini,
            // and skipping it here is what made a single bad check permanent.
            if (_previewProp == null || _previewProp.Model.Hash != requestedHash)
            {
                _previewProp?.Delete();
                _previewProp = null;

                Model tmpModel = ObjectPreview.LoadObject(requestedHash);
                if (tmpModel == null)
                {
                    sender.Items[index].AltTitle = "~r~Invalid";
                    return;
                }

                sender.Items[index].AltTitle = string.Empty;

                switch (_currentObjectType)
                {
                    case ObjectTypes.Prop:
                        _previewProp = World.CreateProp(tmpModel, _objectPreviewPos, false, false);
                        break;
                    case ObjectTypes.Vehicle:
                        _previewProp = World.CreateVehicle(tmpModel, _objectPreviewPos);
                        break;
                    case ObjectTypes.Ped:
                        _previewProp = World.CreatePed(tmpModel, _objectPreviewPos);
                        break;
                }

                if (_previewProp != null)
                {
                    _previewProp.IsPositionFrozen = true;
                    _previewProp.Rotation = new Vector3(0, 0, 180f);
                    if (_previewProp.Model.IsPed)
                        _previewProp.Heading = 180f;
                }

                tmpModel.MarkAsNoLongerNeeded();
            }
        }

        private void OnObjectSelect(object sender, ItemActivatedArgs e)
        {
            var menu = (NativeMenu) sender;

            // Activating the DLC filter must not try to place it as if it were a model.
            if (ReferenceEquals(e.Item, _dlcFilterItem)) return;

            if (PropStreamer.EntityCount == 0)
                _lastAutosave = DateTime.Now;

            _quitWithSearchVisible = _searchMenu.Visible;

            var title = e.Item.Title;
            int objectHash;

            switch (_currentObjectType)
            {
                case ObjectTypes.Prop:
                    if (!ObjectDatabase.MainDb.TryGetValue(title, out objectHash)) return;
                    AddItemToEntityMenu(_snappedProp = PropStreamer.CreateProp(ObjectPreview.LoadObject(objectHash),
                        VectorExtensions.RaycastEverything(new Vector2(0f, 0f)), new Vector3(0, 0, 0), false, force: true,
                        drawDistance: _settings.DrawDistance));
                    break;
                case ObjectTypes.Vehicle:
                    if (!ObjectDatabase.VehicleDb.TryGetValue(title, out objectHash)) return;
                    AddItemToEntityMenu(_snappedProp = PropStreamer.CreateVehicle(ObjectPreview.LoadObject(objectHash),
                        VectorExtensions.RaycastEverything(new Vector2(0f, 0f)), 0f, true, drawDistance: _settings.DrawDistance));
                    break;
                case ObjectTypes.Ped:
                    if (!ObjectDatabase.PedDb.TryGetValue(title, out objectHash)) return;
                    AddItemToEntityMenu(_snappedProp = PropStreamer.CreatePed(ObjectPreview.LoadObject(objectHash),
                        VectorExtensions.RaycastEverything(new Vector2(0f, 0f)), 0f, true, drawDistance: _settings.DrawDistance));
                    if (_snappedProp != null)
                    {
                        PropStreamer.ActiveScenarios.Add(_snappedProp.Handle, "None");
                        PropStreamer.ActiveRelationships.Add(_snappedProp.Handle, DefaultRelationship.ToString());
                        PropStreamer.ActiveWeapons.Add(_snappedProp.Handle, WeaponHash.Unarmed);
                    }
                    break;
            }

            _isChoosingObject = false;
            SetMenuVisible(_objectsMenu, false);
            SetMenuVisible(_searchMenu, false);
            _previewProp?.Delete();
            _previewProp = null;
            _changesMade++;
        }

        /// <summary>
        /// Lists the categories available for <paramref name="type"/>. Picking one fills
        /// <see cref="_objectsMenu"/> via <see cref="OnCategorySelect"/>.
        /// </summary>
        private void RedrawCategoriesMenu(ObjectTypes type = ObjectTypes.Prop)
        {
            var items = ObjectCategories.For(type)
                .Select(category => new NativeItem(category.Name)
                {
                    AltTitle = category.Objects.Count.ToString(CultureInfo.InvariantCulture),
                })
                .ToList();

            _categoriesMenu.Name = "~b~" + Translation.Translate("PLACE") + " " + type.ToString().ToUpper();
            SetMenuItems(_categoriesMenu, null, items, 0);
        }

        private void OnCategorySelect(object sender, ItemActivatedArgs e)
        {
            var categories = ObjectCategories.For(_currentObjectType);
            int index = _categoriesMenu.Items.IndexOf(e.Item);
            if (index < 0 || index >= categories.Count) return;

            var category = categories[index];

            // Favorites is the one category that can be empty, and an empty menu draws as a bare header
            // with nothing to move onto and no row to back out of.
            if (category.Objects.Count == 0)
            {
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate(
                    "No favorites yet. Highlight an object while browsing and press the favorite button to add it."));
                return;
            }

            _currentCategory = category;
            RedrawObjectsMenu(_currentCategory, _currentObjectType);
            _objectsMenu.Name = "~b~" + _currentCategory.Name.ToUpper();

            SetMenuVisible(_categoriesMenu, false);
            SetMenuVisible(_objectsMenu, true);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
        }

        /// <summary>
        /// Hands a menu its rows in one pass, laying it out exactly once.
        /// </summary>
        /// <remarks>
        /// <see cref="NativeMenu.Add(NativeItem)"/> lays the whole menu out again on every call, and that
        /// layout measures each on-screen row's text through game natives. Filling a menu a row at a time
        /// therefore costs one layout per row: the prop list runs to 25,000 of them and is refilled from
        /// scratch whenever the DLC filter changes, which is what stalled the game for seconds. LemonUI
        /// exposes the backing list, so the rows can be dropped in without a layout each and the whole menu
        /// laid out once, by the <see cref="NativeMenu.SelectedIndex"/> setter at the end.
        /// </remarks>
        private static void SetMenuItems(NativeMenu menu, NativeItem header, List<NativeItem> items, int selectedIndex)
        {
            menu.Clear();

            if (header != null)
                menu.Items.Add(header);
            if (items != null)
                menu.Items.AddRange(items);

            if (menu.Items.Count == 0) return;
            menu.SelectedIndex = ClampIndex(selectedIndex, menu.Items.Count);
        }

        /// <summary>
        /// The menu row standing for one model, built once and kept. Every category that lists the model
        /// shares the row: <see cref="_objectsMenu"/> only ever shows one category at a time, so "All" and
        /// the model's own category can hand out the same instance. Rows are worth keeping because building
        /// one is not free either — <see cref="NativeItem"/>'s constructor resolves the screen size through
        /// the game, so the 25,000 props cost 25,000 trips into it.
        /// </summary>
        private NativeItem RowFor(ObjectTypes type, string model)
        {
            Dictionary<string, NativeItem> rows;
            if (!_rowsByModel.TryGetValue(type, out rows))
                _rowsByModel[type] = rows = new Dictionary<string, NativeItem>();

            NativeItem row;
            if (!rows.TryGetValue(model, out row))
            {
                rows[model] = row = new NativeItem(model);
                // Only has to be read here: the row is shared by every list the model appears in, so
                // ToggleFavorite re-badging the one instance keeps all of them in step from then on.
                if (Favorites.IsFavorite(type, model))
                    row.LeftBadgeSet = StarBadge;
            }
            return row;
        }

        /// <summary>
        /// Stars or unstars the highlighted model, from the object list or the search results alike — the
        /// point of the list being to catch the model the player just went looking for.
        /// </summary>
        private void ToggleFavorite()
        {
            var menu = _searchMenu.Visible ? _searchMenu : (_objectsMenu.Visible ? _objectsMenu : null);
            if (menu == null) return;

            int index = menu.SelectedIndex;
            if (index < 0 || index >= menu.Items.Count) return;

            var item = menu.Items[index];
            // The DLC filter shares the object list, and there is no model behind it to star.
            if (ReferenceEquals(item, _dlcFilterItem)) return;

            bool? result = ToggleFavoriteModel(_currentObjectType, item.Title);
            if (result == null) return;

            bool starred = result.Value;
            var favorites = Favorites.CategoryFor(_currentObjectType);

            // Unstarring from inside the favorites list has to take the row out from under the player.
            if (starred || menu != _objectsMenu || !ReferenceEquals(_currentCategory, favorites)) return;

            if (favorites.Objects.Count == 0)
            {
                // Nothing left to browse, and an empty menu offers no way back: step up a level instead.
                _previewProp?.Delete();
                _previewProp = null;
                _currentCategory = null;
                SetMenuVisible(_objectsMenu, false);
                SetMenuVisible(_categoriesMenu, true);
                return;
            }

            // Holding the index rather than the row lands the cursor on whatever moved up into its place.
            RedrawObjectsMenu(favorites, _currentObjectType, index);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
        }

        /// <summary>
        /// Stars or unstars a model and brings everything that shows it back in step, whether the player got to
        /// it through the object list or by aiming at one standing in the world. Returns whether the model ended
        /// up starred, or null when no object list holds it and there is nothing to star.
        /// </summary>
        private bool? ToggleFavoriteModel(ObjectTypes type, string model)
        {
            var db = ObjectDatabase.DbFor(type);
            if (db == null || !db.ContainsKey(model)) return null;

            bool starred = Favorites.Toggle(type, model);

            // Every list the model appears in hands out the one row (see RowFor), so re-badging that instance
            // badges it everywhere at once. There is no row yet if the player has never browsed to the model.
            Dictionary<string, NativeItem> rows;
            NativeItem row;
            if (_rowsByModel.TryGetValue(type, out rows) && rows.TryGetValue(model, out row))
                row.LeftBadgeSet = starred ? StarBadge : null;

            var favorites = Favorites.CategoryFor(type);

            // The category just gained or lost a model, so the rows built for it no longer describe it.
            _rowsByCategory.Remove(favorites);
            RefreshCategoryCount(favorites);

            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + model + "~n~" +
                Translation.Translate(starred ? "Added to Favorites" : "Removed from Favorites"));

            return starred;
        }

        /// <summary>
        /// Re-reads a category's object count onto its row in <see cref="_categoriesMenu"/>. Favorites is
        /// the only category that changes while the menus are up, and the player can return to that menu
        /// without it being rebuilt.
        /// </summary>
        private void RefreshCategoryCount(ObjectCategory category)
        {
            // The rows were built straight off this list, so they line up index for index.
            int index = ObjectCategories.For(_currentObjectType).IndexOf(category);
            if (index < 0 || index >= _categoriesMenu.Items.Count) return;

            _categoriesMenu.Items[index].AltTitle = category.Objects.Count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Re-reads the blacklist onto a prop's row. Rows outlive it — browsing a model that will not load
        /// blacklists it, "Reset Invalid Objects" clears it again — so the flag cannot be baked in at build
        /// time. Only written when it actually changes: the setter lays the row out, which costs a text
        /// measurement, and the overwhelming majority of rows are not flagged and never change.
        /// </summary>
        private static void RefreshInvalidFlag(NativeItem row, int hash)
        {
            var flag = ObjectDatabase.TrackInvalidObjects && ObjectDatabase.InvalidHashes.Contains(hash)
                ? "~r~Invalid"
                : string.Empty;

            if (row.AltTitle != flag)
                row.AltTitle = flag;
        }

        /// <summary>
        /// The rows of <paramref name="category"/>, grouped the way the DLC filter reads them, built on the
        /// first visit and kept. Only prop model names carry a pack prefix; the vehicle and ped lists are
        /// keyed by ScriptHookVDotNet enum names ("Deveste", "Hooker01SFY"), which say nothing about the DLC
        /// that shipped them, so they are left ungrouped and get no filter.
        /// </summary>
        private CategoryRows RowsFor(ObjectCategory category, ObjectTypes type)
        {
            CategoryRows cached;
            if (_rowsByCategory.TryGetValue(category, out cached)) return cached;

            var rows = new CategoryRows();
            var all = new RowGroup();
            rows.ByDlc[Dlc.AllName] = all;

            foreach (var pair in category.Objects)
            {
                var row = RowFor(type, pair.Key);
                all.Items.Add(row);
                all.Hashes.Add(pair.Value);

                if (type != ObjectTypes.Prop) continue;

                var dlc = Dlc.NameFor(pair.Key);
                RowGroup group;
                if (!rows.ByDlc.TryGetValue(dlc, out group))
                    rows.ByDlc[dlc] = group = new RowGroup();
                group.Items.Add(row);
                group.Hashes.Add(pair.Value);
            }

            if (type == ObjectTypes.Prop)
            {
                var present = Dlc.Present(category.Objects.Keys);
                // A single value is always "All DLC" on its own: nothing to filter by.
                if (present.Count > 1)
                    rows.Dlcs.AddRange(present);
            }

            _rowsByCategory[category] = rows;
            return rows;
        }

        /// <summary>
        /// Builds the object list for a category, headed by the DLC filter. <paramref name="selectedIndex"/>
        /// overrides where the cursor lands, for a rebuild that has to leave it where the player put it.
        /// </summary>
        private void RedrawObjectsMenu(ObjectCategory category, ObjectTypes type = ObjectTypes.Prop, int selectedIndex = -1)
        {
            _dlcFilterItem = null;

            if (category == null)
            {
                _objectsMenu.Clear();
                return;
            }

            var rows = RowsFor(category, type);

            if (rows.Dlcs.Count > 0)
            {
                if (!rows.Dlcs.Contains(_dlcFilter))
                    _dlcFilter = Dlc.AllName;

                _dlcFilterItem = new NativeListItem<string>(Translation.Translate("DLC"), rows.Dlcs.ToArray())
                {
                    SelectedIndex = rows.Dlcs.IndexOf(_dlcFilter),
                };
                _dlcFilterItem.ItemChanged += (sender, e) =>
                {
                    _dlcFilter = e.Object;
                    FillObjectsMenu(rows, type, false);
                };
            }

            FillObjectsMenu(rows, type, true, selectedIndex);
        }

        /// <summary>
        /// Lists the rows the DLC filter lets through, under the filter row itself. The filter item is
        /// re-added rather than rebuilt: this also runs from that item's own ItemChanged, so the instance
        /// the menu is part-way through handling has to survive the refill.
        /// </summary>
        private void FillObjectsMenu(CategoryRows rows, ObjectTypes type, bool selectFirstObject, int selectedIndex = -1)
        {
            // Picking the filter's group is the whole cost of a filter change now: the rows behind it were
            // built and sorted into it when the category was first opened.
            RowGroup group;
            if (_dlcFilterItem == null || !rows.ByDlc.TryGetValue(_dlcFilter, out group))
                group = rows.ByDlc[Dlc.AllName];

            // Only props are validated against the game, so only they can be flagged.
            if (type == ObjectTypes.Prop)
            {
                for (int i = 0; i < group.Items.Count; i++)
                    RefreshInvalidFlag(group.Items[i], group.Hashes[i]);
            }

            if (_dlcFilterItem != null)
            {
                _dlcFilterItem.Description = string.Format(CultureInfo.InvariantCulture, "{0} ({1})",
                    Translation.Translate("Show only the objects one DLC added."), group.Items.Count);
            }

            // Opening a category lands on its first model, so that it previews something right away; a
            // change of filter keeps the cursor where the player left it, on the filter row.
            var selected = selectedIndex >= 0
                ? selectedIndex
                : selectFirstObject && _dlcFilterItem != null && group.Items.Count > 0 ? 1 : 0;
            SetMenuItems(_objectsMenu, _dlcFilterItem, group.Items, selected);
        }

        private bool ApplySearchQuery(string searchQuery, string modelName)
        {
            var q = searchQuery.ToLower();
            if (q.Contains(" or "))
            {
                var queries = Regex.Split(q, "\\s+or\\s+");
                return queries.Aggregate(false, (current, query) => current || ApplySearchQuery(query, modelName));
            }

            if (q.Contains(" and "))
            {
                var queries = Regex.Split(q, "\\s+and\\s+");
                return queries.Aggregate(true, (current, query) => current && ApplySearchQuery(query, modelName));
            }

            return modelName.ToLower().Contains(q);
        }

        /// <summary>
        /// Lists every model matching the query. A loose query matches thousands of them, so this fills the
        /// menu the same way a category does: shared rows, laid out in one pass. See <see cref="SetMenuItems"/>.
        /// </summary>
        private void RedrawSearchMenu(string searchQuery, ObjectTypes type = ObjectTypes.Prop)
        {
            var results = new List<NativeItem>();

            switch (type)
            {
                case ObjectTypes.Prop:
                    foreach (var u in ObjectDatabase.MainDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                    {
                        var row = RowFor(type, u.Key);
                        RefreshInvalidFlag(row, u.Value);
                        results.Add(row);
                    }
                    break;
                case ObjectTypes.Vehicle:
                    foreach (var u in ObjectDatabase.VehicleDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                        results.Add(RowFor(type, u.Key));
                    break;
                case ObjectTypes.Ped:
                    foreach (var u in ObjectDatabase.PedDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                        results.Add(RowFor(type, u.Key));
                    break;
            }

            SetMenuItems(_searchMenu, null, results, 0);
        }

        private string GetSafeShortReverseString(string input, int limit)
        {
            if (input == null) return null;
            if (input.Length > limit)
                return "..." + input.Substring(input.Length - limit, limit);
            return input;
        }

        private string GetSafeShortString(string input, int limit)
        {
            if (input == null) return null;
            if (input.Length > limit)
                return input.Substring(0, limit) + "...";
            return input;
        }

        private void RedrawFilepickerMenu(string folder = null)
        {
            // Saved maps all live in one folder, so the picker opens there instead of at the game's root. The
            // ".." row still leads back out for a map that came from somewhere else.
            if (folder == null) folder = Path.GetFullPath(UserMaps.EnsureFolder());
            _filepicker.Clear();
            _filepicker.Name = "~b~" + GetSafeShortReverseString(folder, 30);

            var backup = new NativeItem("..");
            backup.LeftBadgeSet = FolderBadge;
            backup.Activated += (sender, item) =>
            {
                RedrawFilepickerMenu(Directory.GetParent(folder).ToString());
            };

            if (Directory.GetParent(folder) == null)
                backup.Enabled = false;

            _filepicker.Add(backup);

            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var dirItem = new NativeItem(GetSafeShortString(Path.GetFileName(directory), 40));
                dirItem.LeftBadgeSet = FolderBadge;
                dirItem.Activated += (sender, item) =>
                {
                    RedrawFilepickerMenu(directory);
                };

                _filepicker.Add(dirItem);
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var item = new NativeItem(GetSafeShortString(Path.GetFileName(file), 40));

                MapSerializer.Format mapFormat;
                string description = "";

                if (file.EndsWith(".ini"))
                {
                    mapFormat = MapSerializer.Format.SimpleTrainer;
                }
                else if (file.EndsWith(".SP00N"))
                {
                    mapFormat = MapSerializer.Format.SpoonerLegacy;
                }
                else if (file.EndsWith(".xml"))
                {
                    mapFormat = MapSerializer.Format.NormalXml;
                    Map map = null;

                    try
                    {
                        var ser = new XmlSerializer(typeof(Map));
                        using (var stream = File.OpenRead(file))
                            map = (Map) ser.Deserialize(stream);
                    }
                    catch (Exception) {}

                    if (map == null)
                    {
                        try
                        {
                            var spReader = new XmlSerializer(typeof(MenyooCompatibility.SpoonerPlacements));
                            MenyooCompatibility.SpoonerPlacements newMap;
                            using (var stream = File.OpenRead(file))
                                newMap = (MenyooCompatibility.SpoonerPlacements)spReader.Deserialize(stream);

                            if (newMap != null)
                            {
                                description = "~h~Format:~h~ Menyoo Trainer";
                                mapFormat = MapSerializer.Format.Menyoo;
                            }
                        }
                        catch (Exception) { }
                    }

                    if (map?.Metadata != null)
                    {
                        description = "~h~Format:~h~ Map Editor\n~h~Name:~h~ " + map.Metadata.Name + "\n~h~Author:~h~ " +
                                      map.Metadata.Creator + "\n~h~Description:~h~ " + map.Metadata.Description;
                    }
                }
                else
                {
                    continue;
                }

                item.Description = description;

                var chosenFormat = mapFormat;
                item.Activated += (sender, selectedItem) =>
                {
                    SetMenuVisible(_filepicker, false);
                    LoadMap(file, chosenFormat);
                };

                _filepicker.Add(item);
            }

            if (_filepicker.Items.Count > 0)
                _filepicker.SelectedIndex = 0;
        }

        /// <summary>
        /// One row per autoloaded map, and the red row underneath that takes them all at once. Only ever reached
        /// with more than one map loaded — <see cref="UnloadAutoloadedMaps"/> unloads a lone map on the spot.
        /// </summary>
        private void RedrawUnloadAutoloadedMenu()
        {
            _unloadAutoloadedMenu.Clear();

            var names = AutoloadedMaps.Names.ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var index = i;
                var item = new NativeItem(GetSafeShortString(names[i], 40),
                    Translation.Translate("Remove this map from the world. The other autoloaded maps stay."));

                item.Activated += (sender, args) =>
                {
                    UnloadAutoloadedMap(index);
                    LeaveUnloadAutoloadedMenu();
                };

                _unloadAutoloadedMenu.Add(item);
            }

            var allItem = new NativeItem("~r~" + Translation.Translate("Unload All Maps"),
                Translation.Translate("Remove every autoloaded map from the world."));

            allItem.Activated += (sender, args) =>
            {
                UnloadAllAutoloadedMaps();
                LeaveUnloadAutoloadedMenu();
            };

            _unloadAutoloadedMenu.Add(allItem);
            _unloadAutoloadedMenu.SelectedIndex = 0;
        }

        /// <summary>
        /// A map just went, so the rows below it are stale and their indices have shifted: the list is rebuilt
        /// around what is left. Once fewer than two maps remain there is nothing to choose between, and the menu
        /// hands back to the main menu, whose row unloads the last map outright.
        /// </summary>
        private void LeaveUnloadAutoloadedMenu()
        {
            if (AutoloadedMaps.MapCount > 1)
            {
                RedrawUnloadAutoloadedMenu();
                return;
            }

            SetMenuVisible(_unloadAutoloadedMenu, false);
            SetMenuVisible(_mainMenu, true);
        }

        private void RedrawMetadataMenu()
        {
            _metadataMenu.Clear();

            var saveItem = new NativeItem(Translation.Translate("Save Map"));

            saveItem.Activated += (sender, item) =>
            {
                SaveMap(PropStreamer.CurrentMapMetadata.Filename, MapSerializer.Format.NormalXml);
                SetMenuVisible(_metadataMenu, false);
            };

            if (string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Filename))
                saveItem.Enabled = false;

            {
                var filenameItem = new NativeItem(Translation.Translate("File Path"));

                if (string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Filename))
                    filenameItem.RightBadgeSet = AlertBadge;
                else
                    filenameItem.AltTitle = GetSafeShortReverseString(PropStreamer.CurrentMapMetadata.Filename, 20);

                filenameItem.Activated += (sender, item) =>
                {
                    var newName = Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Filename ?? "", 255);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    if (!newName.EndsWith(".xml")) newName += ".xml";
                    PropStreamer.CurrentMapMetadata.Filename = newName;
                    saveItem.Enabled = true;

                    filenameItem.RightBadgeSet = null;
                    filenameItem.AltTitle = GetSafeShortReverseString(newName, 20);
                };

                _metadataMenu.Add(filenameItem);
            }

            {
                var nameItem = new NativeItem(Translation.Translate("Map Name"));

                if (!string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Name))
                    nameItem.AltTitle = GetSafeShortString(PropStreamer.CurrentMapMetadata.Name, 20);

                nameItem.Activated += (sender, item) =>
                {
                    var newName = Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Name ?? "", 30);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    PropStreamer.CurrentMapMetadata.Name = newName;
                    nameItem.AltTitle = GetSafeShortString(newName, 20);
                };

                _metadataMenu.Add(nameItem);
            }

            {
                var authorItem = new NativeItem(Translation.Translate("Author"));

                if (!string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Creator))
                    authorItem.AltTitle = GetSafeShortString(PropStreamer.CurrentMapMetadata.Creator, 20);

                authorItem.Activated += (sender, item) =>
                {
                    var newName = Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Creator ?? "", 30);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    PropStreamer.CurrentMapMetadata.Creator = newName;
                    authorItem.AltTitle = GetSafeShortString(newName, 20);
                };

                _metadataMenu.Add(authorItem);
            }

            {
                var descItem = new NativeItem(Translation.Translate("Description"));

                if (!string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Description))
                    descItem.Description = PropStreamer.CurrentMapMetadata.Description;

                descItem.Activated += (sender, item) =>
                {
                    var newName = Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Description ?? "", 255);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    PropStreamer.CurrentMapMetadata.Description = newName;
                    descItem.Description = newName;
                };

                _metadataMenu.Add(descItem);
            }

            {
                var autoloadItem = new NativeCheckboxItem(Translation.Translate("Autoload This Map"),
                    Translation.Translate("Spawn this map whenever the script starts. Autoloaded maps stay out of the map you are editing; unload them from the main menu."),
                    PropStreamer.CurrentMapMetadata.Autoload);

                autoloadItem.CheckboxChanged += (sender, item) =>
                {
                    PropStreamer.CurrentMapMetadata.Autoload = autoloadItem.Checked;
                };

                _metadataMenu.Add(autoloadItem);
            }

            _metadataMenu.Add(saveItem);

            _metadataMenu.SelectedIndex = saveItem.Enabled ? _metadataMenu.Items.IndexOf(saveItem) : 0;
        }

        private void RedrawFormatMenu()
        {
            _formatMenu.Clear();
            _formatMenu.Add(new NativeItem("XML", Translation.Translate("Default format for Map Editor. Choose this one if you have no idea. This saves props, vehicles and peds.")));
            _formatMenu.Add(new NativeItem("Simple Trainer",
                Translation.Translate("Format used in Simple Trainer mod (objects.ini). Only saves props.")));
            if (_savingMap)
            {
                _formatMenu.Add(new NativeItem(Translation.Translate("C# Code"),
                    Translation.Translate("Directly outputs to C# code to spawn your entities. Saves props, vehicles and peds.")));
                _formatMenu.Add(new NativeItem(Translation.Translate("Raw"),
                    Translation.Translate("Writes the entity and their position and rotation. Useful for taking coordinates.")));
            }
            _formatMenu.Add(new NativeItem("Spooner (Legacy)",
                Translation.Translate("Format used in Object Spooner mod (.SP00N).")));
            _formatMenu.Add(new NativeItem("Menyoo", Translation.Translate("Format used in Meynoo mod (.xml).")));

            if (!_savingMap)
                _formatMenu.Add(new NativeItem(Translation.Translate("File Chooser...")));

            _formatMenu.SelectedIndex = 0;
        }

        public void AddItemToEntityMenu(MapObject obj)
        {
            if (obj == null) return;
            var name = ObjectDatabase.MainDb.ContainsValue(obj.Hash) ? ObjectDatabase.MainDb.First(pair => pair.Value == obj.Hash).Key : "Unknown World Prop";
            _currentObjectsMenu.Add(new NativeItem("~h~[WORLD]~h~ " + name)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.World, WorldId = obj.Id },
            });
        }

        public void AddItemToEntityMenu(Marker mark)
        {
            if (mark == null) return;
            _currentObjectsMenu.Add(new NativeItem("~h~[MARK]~h~ " + mark.Type)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Marker, Id = mark.Id },
            });
        }

        public void AddItemToEntityMenu(DynamicPickup pickup)
        {
            if (pickup == null) return;
            _currentObjectsMenu.Add(new NativeItem("~h~[PICKUP]~h~ " + pickup.PickupName)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Pickup, Id = pickup.UID },
            });
        }

        public void AddItemToEntityMenu(Entity ent)
        {
            if (ent == null) return;

            var type = ent is Vehicle ? ObjectTypes.Vehicle : ent is Ped ? ObjectTypes.Ped : ObjectTypes.Prop;
            _currentObjectsMenu.Add(new NativeItem(EntityMenuTitle(type, ent.Model.Hash))
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Entity, Id = ent.Handle },
            });
        }

        /// <summary>
        /// The row for an object of the map that is not in the world at the moment, loaded from a file with the
        /// player nowhere near it. It reads exactly like the row it will be once it has been walked up to; the
        /// player has no reason to care which of the two it is, and activating it puts it back either way.
        /// </summary>
        public void AddStreamedItemToEntityMenu(MapObject o, int uid)
        {
            if (o == null) return;

            _currentObjectsMenu.Add(new NativeItem(EntityMenuTitle(o.Type, o.Hash))
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Streamed, Id = uid },
            });
        }

        private static string EntityMenuTitle(ObjectTypes type, int hash)
        {
            switch (type)
            {
                case ObjectTypes.Vehicle:
                    return "~h~[VEH]~h~ " + (ObjectDatabase.VehicleDb.ContainsValue(hash)
                        ? ObjectDatabase.VehicleDb.First(x => x.Value == hash).Key.ToUpper()
                        : "Unknown Vehicle");
                case ObjectTypes.Ped:
                    return "~h~[PED]~h~ " + (ObjectDatabase.PedDb.ContainsValue(hash)
                        ? ObjectDatabase.PedDb.First(x => x.Value == hash).Key.ToUpper()
                        : "Unknown Ped");
                default:
                    return "~h~[PROP]~h~ " + (ObjectDatabase.MainDb.ContainsValue(hash)
                        ? ObjectDatabase.MainDb.First(pair => pair.Value == hash).Key
                        : "Unknown Prop");
            }
        }

        private void RemoveFromEntityMenu(Func<EntityMenuTag, bool> predicate)
        {
            var found = _currentObjectsMenu.Items.FirstOrDefault(item => item.Tag is EntityMenuTag tag && predicate(tag));
            if (found == null) return;
            _currentObjectsMenu.Remove(found);
            if (_currentObjectsMenu.Items.Count == 0)
                SetMenuVisible(_currentObjectsMenu, false);
        }

        public void RemoveItemFromEntityMenu(Entity ent)
        {
            if (PropStreamer.IsPickup(ent.Handle))
            {
                var uid = PropStreamer.GetPickup(ent.Handle).UID;
                RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Pickup && tag.Id == uid);
            }
            else
            {
                RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Entity && tag.Id == ent.Handle);
            }
        }

        public void RemoveItemFromEntityMenu(string id)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.World && tag.WorldId == id);
        }

        public void RemoveMarkerFromEntityMenu(int id)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Marker && tag.Id == id);
        }

        public void OnEntityTeleport(object sender, ItemActivatedArgs e)
        {
            if (!IsInFreecam) return;
            if (!(e.Item.Tag is EntityMenuTag tag)) return;

            switch (tag.Kind)
            {
                case EntityMenuKind.Pickup:
                {
                    var pickup = PropStreamer.GetPickupByUID(tag.Id);
                    if (pickup == null) return;
                    if (_settings.SnapCameraToSelectedObject)
                    {
                        _mainCamera.Position = pickup.RealPosition + new Vector3(5f, 5f, 10f);
                        _mainCamera.PointAt(pickup.RealPosition);
                    }
                    CloseAllMenus();
                    Script.Wait(300);
                    _selectedProp = Compat.Ent(pickup.ObjectHandle);
                    RedrawObjectInfoMenu(_selectedProp, true);
                    SetMenuVisible(_objectInfoMenu, true);
                    return;
                }
                case EntityMenuKind.World:
                {
                    var mapObj = PropStreamer.RemovedObjects.FirstOrDefault(obj => obj.Id == tag.WorldId);
                    if (mapObj == null) return;
                    var t = World.CreateProp(mapObj.Hash, mapObj.Position, mapObj.Rotation, true, false);
                    t.PositionNoOffset = mapObj.Position;
                    CloseAllMenus();
                    RemoveItemFromEntityMenu(mapObj.Id);
                    PropStreamer.RemovedObjects.Remove(mapObj);
                    return;
                }
                case EntityMenuKind.Marker:
                {
                    Marker tmpM = PropStreamer.Markers.FirstOrDefault(m => m.Id == tag.Id);
                    if (tmpM == null) return;
                    _mainCamera.Position = tmpM.Position + new Vector3(5f, 5f, 10f);
                    if (_settings.SnapCameraToSelectedObject)
                        _mainCamera.PointAt(tmpM.Position);
                    CloseAllMenus();
                    _selectedMarker = tmpM;
                    RedrawObjectInfoMenu(_selectedMarker, true);
                    SetMenuVisible(_objectInfoMenu, true);
                    return;
                }
                case EntityMenuKind.Streamed:
                {
                    // Picked from the list rather than flown to, so it is somewhere the player is not and is
                    // not standing anywhere at the moment. It goes back into the world here and is then no
                    // different from any other row; holding it as the selected entity is what keeps it there
                    // while the player works on it, however far away that is.
                    var record = PropStreamer.StreamedOut.FirstOrDefault(s => s.Uid == tag.Id);
                    if (record == null) return;

                    var restored = StreamInNow(record);
                    if (restored == null) return;

                    SelectFromEntityMenu(restored);
                    return;
                }
                default:
                {
                    var prop = Compat.Ent(tag.Id);
                    if (prop == null || !prop.Exists()) return;
                    SelectFromEntityMenu(prop);
                    return;
                }
            }
        }

        private void SelectFromEntityMenu(Entity entity)
        {
            if (_settings.SnapCameraToSelectedObject)
            {
                _mainCamera.Position = entity.Position + new Vector3(5f, 5f, 10f);
                _mainCamera.PointAt(entity, Vector3.Zero);
            }
            CloseAllMenus();
            _selectedProp = entity;
            RedrawObjectInfoMenu(_selectedProp, true);
            SetMenuVisible(_objectInfoMenu, true);
        }
    }
}
