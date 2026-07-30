using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace MapEditor
{
	public class MapSerializer
	{
		public enum Format
		{
			NormalXml,
			SimpleTrainer,
			CSharpCode,
            SpoonerLegacy,
            Menyoo,
			Raw,
		}

	    public float Parse(string floatVal)
	    {
	        return float.Parse(floatVal, CultureInfo.InvariantCulture);
	    }

		internal Map Deserialize(string path, Format format)
		{
			string tip = "";
			switch (format)
			{
				case Format.NormalXml:
					XmlSerializer reader = new XmlSerializer(typeof (Map));
					var file = new StreamReader(path);
					var map = (Map) reader.Deserialize(file);
					file.Close();
					return map;
                case Format.Menyoo:
                    var spReader = new XmlSerializer(typeof(MenyooCompatibility.SpoonerPlacements));
                    var spFile = new StreamReader(path);
			        var spMap = (MenyooCompatibility.SpoonerPlacements) spReader.Deserialize(spFile);
                    spFile.Close();

                    var outputMap = new Map();

			        foreach (var placement in spMap.Placement)
			        {
                        var obj = new MapObject();
                        switch (placement.Type)
			            {
                            case 3: // Props
			                    {
                                    obj.Type = ObjectTypes.Prop;
                                }
			                    break;
                            case 1: // Peds
                                {
                                    
                                    obj.Type = ObjectTypes.Ped;
                                }
                                break;
                            case 2: // Vehicles
                                {
                                    obj.Type = ObjectTypes.Vehicle;
                                }
                                break;
                        }
                        obj.Dynamic = placement.Dynamic;
                        obj.Hash = Convert.ToInt32(placement.ModelHash, 16);
                        obj.Position = new Vector3(placement.PositionRotation.X, placement.PositionRotation.Y, placement.PositionRotation.Z);
                        obj.Rotation = new Vector3(placement.PositionRotation.Pitch, placement.PositionRotation.Roll, placement.PositionRotation.Yaw);
                        outputMap.Objects.Add(obj);
                    }
			        return outputMap;
				case Format.SimpleTrainer:
			    {
			        var tmpMap = new Map();
			        string currentSection = "";
			        string oldSection = "";
			        Dictionary<string, string> tmpData = new Dictionary<string, string>();
			        foreach (string line in File.ReadAllLines(path))
			        {
			            if (line.StartsWith("[") && line.EndsWith("]"))
			            {
			                oldSection = currentSection;
			                currentSection = line;
			                tip = currentSection;
			                if (oldSection == "" || oldSection == "[Player]") continue;
			                Vector3 pos = new Vector3(float.Parse(tmpData["x"], CultureInfo.InvariantCulture),
			                    float.Parse(tmpData["y"], CultureInfo.InvariantCulture),
			                    float.Parse(tmpData["z"], CultureInfo.InvariantCulture));
			                Vector3 rot = new Vector3(float.Parse(tmpData["qz"]), float.Parse(tmpData["qw"]), float.Parse(tmpData["h"]));
			                Quaternion q = new Quaternion()
			                {
			                    X = Parse(tmpData["qx"]),
			                    Y = Parse(tmpData["qy"]),
			                    Z = Parse(tmpData["qz"]),
			                    W = Parse(tmpData["qw"]),
			                };
			                int mod = Convert.ToInt32(tmpData["Model"], CultureInfo.InvariantCulture);
			                int dyn = Convert.ToInt32(tmpData["Dynamic"], CultureInfo.InvariantCulture);
			                tmpMap.Objects.Add(new MapObject()
			                {
			                    Hash = mod,
			                    Position = pos,
			                    Rotation = rot,
			                    Dynamic = dyn == 1,
			                    Quaternion = q
			                });
			                tmpData = new Dictionary<string, string>();
			                continue;
			            }
			            if (currentSection == "[Player]") continue;
			            string[] spl = line.Split('=');
			            tmpData.Add(spl[0], spl[1]);
			        }
			        Vector3 lastPos = new Vector3(float.Parse(tmpData["x"], CultureInfo.InvariantCulture),
			            float.Parse(tmpData["y"], CultureInfo.InvariantCulture), float.Parse(tmpData["z"], CultureInfo.InvariantCulture));
			        Vector3 lastRot = new Vector3(float.Parse(tmpData["qz"]), float.Parse(tmpData["qw"]), float.Parse(tmpData["h"]));
			        Quaternion lastQ = new Quaternion()
			        {
			            X = Parse(tmpData["qx"]),
			            Y = Parse(tmpData["qy"]),
			            Z = Parse(tmpData["qz"]),
			            W = Parse(tmpData["qw"]),
			        };
			        int lastMod = Convert.ToInt32(tmpData["Model"], CultureInfo.InvariantCulture);
			        int lastDyn = Convert.ToInt32(tmpData["Dynamic"], CultureInfo.InvariantCulture);
			        tmpMap.Objects.Add(new MapObject()
			        {
			            Hash = lastMod,
			            Position = lastPos,
			            Rotation = lastRot,
			            Dynamic = lastDyn == 1,
			            Quaternion = lastQ
			        });
			        return tmpMap;
			    }
                case Format.SpoonerLegacy:
			    {
                        var tmpMap = new Map();
                        string currentSection = "";
                        string oldSection = "";
                        Dictionary<string, string> tmpData = new Dictionary<string, string>();
                        foreach (string line in File.ReadAllLines(path))
                        {
                            if (line.StartsWith("[") && line.EndsWith("]"))
                            {
                                oldSection = currentSection;
                                currentSection = line;
                                tip = currentSection;
                                if (!tmpData.ContainsKey("Type")) continue;
                                Vector3 pos = new Vector3(float.Parse(tmpData["X"], CultureInfo.InvariantCulture),
                                    float.Parse(tmpData["Y"], CultureInfo.InvariantCulture),
                                    float.Parse(tmpData["Z"], CultureInfo.InvariantCulture));
                                Vector3 rot = new Vector3(float.Parse(tmpData["Pitch"]), float.Parse(tmpData["Roll"]), float.Parse(tmpData["Yaw"]));
                                
                                int mod = Convert.ToInt32("0x" + tmpData["Hash"], 16);
                                tmpMap.Objects.Add(new MapObject()
                                {
                                    Hash = mod,
                                    Position = pos,
                                    Rotation = rot,
                                    Type = tmpData["Type"] == "1" ? ObjectTypes.Ped : tmpData["Type"] == "2" ? ObjectTypes.Vehicle : ObjectTypes.Prop,
                                });
                                tmpData = new Dictionary<string, string>();
                                continue;
                            }
                            string[] spl = line.Split('=');
                            if (spl.Length >= 2)
                                tmpData.Add(spl[0].Trim(), spl[1].Trim());
                        }
                        return tmpMap;
                    }
				default:
					throw new NotImplementedException("This is not implemented yet.");
			}
		}

		internal void Serialize(string path, Map map, Format format)
		{
			if(path == null) return;
			switch (format)
			{
				case Format.NormalXml:
					XmlSerializer writer = new XmlSerializer(typeof(Map));
					map.Objects.RemoveAll(mo => mo.Position == new Vector3(0, 0, 0));
					var file = new StreamWriter(path);
					writer.Serialize(file, map);
					file.Close();
					break;
				case Format.SimpleTrainer:
					string mainOutput = "[Player]\r\n" +
					                    "Teleport=0\r\n" +
					                    "x=0\r\n" +
					                    "y=0\r\n" +
					                    "z=0\r\n";
					int count = 1;
					for (int i = 0; i < map.Objects.Count; i++)
					{
						if(map.Objects[i].Position == new Vector3(0, 0, 0)) continue;
						mainOutput += "[" + count + "]\r\n";
						mainOutput += "x=" + map.Objects[i].Position.X.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "y=" + map.Objects[i].Position.Y.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "z=" + map.Objects[i].Position.Z.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "h=" + map.Objects[i].Rotation.Z.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "Model=" + map.Objects[i].Hash + "\r\n";
						mainOutput += "qx=" + map.Objects[i].Quaternion.X.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "qy=" + map.Objects[i].Quaternion.Y.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "qz=" + map.Objects[i].Quaternion.Z.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "qw=" + map.Objects[i].Quaternion.W.ToString(CultureInfo.InvariantCulture) + "\r\n";
						mainOutput += "offz=0\r\n";
						mainOutput += "Dynamic=" + (map.Objects[i].Dynamic ? "1\n" : "0\n");
						count++;
					}
					File.WriteAllText(path, mainOutput);
					break;
                case Format.SpoonerLegacy:
			        string main = "";
                    int c = 1;
                    for (int i = 0; i < map.Objects.Count; i++)
                    {
                        if (map.Objects[i].Position == new Vector3(0, 0, 0)) continue;
                        main += "[" + c + "]\r\n";
                        main += "Type = " + (map.Objects[i].Type == ObjectTypes.Ped ? 1 : map.Objects[i].Type == ObjectTypes.Vehicle ? 2 : 3) + "\r\n";
                        main += "Hash = " + map.Objects[i].Hash.ToString("x8") + "\r\n";
                        main += "X = " + map.Objects[i].Position.X.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Y = " + map.Objects[i].Position.Y.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Z = " + map.Objects[i].Position.Z.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Pitch = " + map.Objects[i].Rotation.X.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Roll = " + map.Objects[i].Rotation.Y.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Yaw = " + map.Objects[i].Rotation.Z.ToString(CultureInfo.InvariantCulture) + "\r\n";
                        main += "Opacity = 0x000000ff\r\n";
                        c++;
                    }
                    File.WriteAllText(path, main);
                    break;
				case Format.CSharpCode:
					var props = new StringBuilder();
					var vehicles = new StringBuilder();
					var peds = new StringBuilder();
					var pickups = new StringBuilder();
					var markers = new StringBuilder();
					var removedFromWorld = new StringBuilder();

					foreach (var o in map.Objects)
					{
						if (o.Position == new Vector3(0, 0, 0)) continue;

						switch (o.Type)
						{
							case ObjectTypes.Prop:
							{
								// A door hangs on its own hinge: it is spawned static so the game does not drop it,
								// then unfrozen so the player can still swing it.
								var dynamic = o.Dynamic && !o.Door;
								var frozen = !o.Dynamic && !o.Door;

								props.AppendFormat("        prop = World.CreatePropNoOffset(new Model({0}), {1}, {2}, {3});\r\n",
									o.Hash, Vec(o.Position), Vec(o.Rotation), Bool(dynamic));
								props.AppendFormat("        if (prop != null) {{ prop.LodDistance = LodDistance; prop.IsPositionFrozen = {0}; _entities.Add(prop); }}\r\n",
									Bool(frozen));
								break;
							}
							case ObjectTypes.Vehicle:
							{
								vehicles.AppendFormat("        vehicle = World.CreateVehicle(new Model({0}), {1}, {2});\r\n",
									o.Hash, Vec(o.Position), Float(o.Rotation.Z));
								vehicles.AppendFormat("        if (vehicle != null) {{ vehicle.LodDistance = LodDistance; vehicle.Mods.PrimaryColor = (VehicleColor){0}; vehicle.Mods.SecondaryColor = (VehicleColor){1}; vehicle.IsSirenActive = {2}; vehicle.IsPositionFrozen = {3}; _entities.Add(vehicle); }}\r\n",
									o.PrimaryColor, o.SecondaryColor, Bool(o.SirensActive), Bool(!o.Dynamic));

								// The game reports no liveries on a vehicle with no mod kit installed, so one goes on first.
								if (o.Livery >= 0)
									vehicles.AppendFormat("        if (vehicle != null) {{ Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle, 0); vehicle.Mods.Livery = {0}; }}\r\n",
										o.Livery);
								break;
							}
							case ObjectTypes.Ped:
							{
								// Peds are placed by their feet, props by their centre: the editor stores the centre.
								var position = o.Position - new Vector3(0f, 0f, 1f);

								peds.AppendFormat("        ped = World.CreatePed(new Model({0}), {1}, {2});\r\n",
									o.Hash, Vec(position), Float(o.Rotation.Z));
								peds.Append("        if (ped != null)\r\n        {\r\n");
								peds.AppendFormat("            ped.IsPositionFrozen = {0};\r\n", Bool(!o.Dynamic));

								if (o.Weapon.HasValue && o.Weapon.Value != WeaponHash.Unarmed)
									peds.AppendFormat("            ped.Weapons.Give({0}, 999, true, true);\r\n", WeaponLiteral(o.Weapon.Value));

								if (o.Drawables != null)
								{
									for (int slot = 0; slot < o.Drawables.Length; slot++)
									{
										int texture = o.Textures != null && slot < o.Textures.Length ? o.Textures[slot] : 0;
										peds.AppendFormat("            Function.Call(Hash.SET_PED_COMPONENT_VARIATION, ped, {0}, {1}, {2}, 0);\r\n",
											slot, o.Drawables[slot], texture);
									}
								}

								foreach (var line in ScenarioLines(o.Action))
									peds.AppendFormat("            {0}\r\n", line);

								peds.Append("            _entities.Add(ped);\r\n        }\r\n");
								break;
							}
							case ObjectTypes.Pickup:
							{
								// No SHVDN wrapper takes a bare pickup hash: CreatePickup wants a PickupType plus the
								// model to stand in for it, and the editor only ever knew the hash.
								pickups.AppendFormat("        _pickups.Add(Function.Call<int>(Hash.CREATE_PICKUP_ROTATE, {0}, {1}, {2}, {3}, 0f, 0f, {4}, 515, {5}, 0, false, 0));\r\n",
									o.Hash, Float(o.Position.X), Float(o.Position.Y), Float(o.Position.Z), Float(o.Rotation.Z), o.Amount);
								break;
							}
						}
					}

					foreach (var marker in map.Markers)
					{
						markers.AppendFormat("        World.DrawMarker((MarkerType){0}, {1}, Vector3.Zero, {2}, {3}, Color.FromArgb({4}, {5}, {6}, {7}), {8}, {9});\r\n",
							(int) marker.Type, Vec(marker.Position), Vec(marker.Rotation), Vec(marker.Scale),
							marker.Alpha, marker.Red, marker.Green, marker.Blue,
							Bool(marker.BobUpAndDown), Bool(marker.RotateToCamera));
					}

					foreach (var o in map.RemoveFromWorld)
					{
						removedFromWorld.AppendFormat("        worldProp = World.GetClosestProp({0}, 1f, new Model({1}));\r\n",
							Vec(o.Position), o.Hash);
						removedFromWorld.Append("        if (worldProp != null && worldProp.Exists() && !_entities.Contains(worldProp)) worldProp.Delete();\r\n");
					}

					// Declaring a variable that no object ends up assigning would only draw a compiler warning in
					// the map the player is handed.
					var declarations = new StringBuilder();
					if (props.Length > 0) declarations.Append("        Prop prop;\r\n");
					if (vehicles.Length > 0) declarations.Append("        Vehicle vehicle;\r\n");
					if (peds.Length > 0) declarations.Append("        Ped ped;\r\n");
					if (declarations.Length > 0) declarations.Append("\r\n");

					string finalOutput = string.Format(@"using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;

/// <summary>
/// Generated by Map Editor. Drop this file into your scripts folder and the map spawns with the game.
/// </summary>
public class MapEditorGeneratedMap : Script
{{
    /// <summary>How far away the map's props stay drawn, in metres.</summary>
    private const int LodDistance = 3000;

    private readonly List<Entity> _entities = new List<Entity>();
    private readonly List<int> _pickups = new List<int>();
    private bool _spawned;

    public MapEditorGeneratedMap()
    {{
        Tick += OnTick;
        Aborted += OnAborted;
    }}

    private void OnTick(object sender, EventArgs e)
    {{
        if (!_spawned)
        {{
            SpawnMap();
            _spawned = true;
        }}

        DrawMarkers();
        RemoveWorldObjects();
    }}

    /// <summary>
    /// The map belongs to this script, so it leaves with it: without this a reload would stack a second
    /// copy of every prop on top of the first.
    /// </summary>
    private void OnAborted(object sender, EventArgs e)
    {{
        foreach (Entity entity in _entities)
        {{
            if (entity != null && entity.Exists())
                entity.Delete();
        }}

        foreach (int pickup in _pickups)
            Function.Call(Hash.REMOVE_PICKUP, pickup);

        _entities.Clear();
        _pickups.Clear();
    }}

    private void SpawnMap()
    {{
{0}        /* PROPS */
{1}
        /* VEHICLES */
{2}
        /* PEDS */
{3}
        /* PICKUPS */
{4}    }}

    /// <summary>Markers are not entities: they only exist for the frame they are drawn in.</summary>
    private void DrawMarkers()
    {{
{5}    }}

    /// <summary>
    /// The game streams its own props back in as the player comes and goes, so the ones the map deletes
    /// have to be swept every tick rather than once at startup.
    /// </summary>
    private void RemoveWorldObjects()
    {{
{6}{7}    }}
}}",
						declarations,
						props,
						vehicles,
						peds,
						pickups,
						markers,
						removedFromWorld.Length > 0 ? "        Prop worldProp;\r\n\r\n" : "",
						removedFromWorld);

					File.WriteAllText(path, finalOutput);
					break;
				case Format.Raw:
					var raw = new StringBuilder();
					var rawCounter = 1;
					foreach (var prop in map.Objects)
					{
						if (prop.Position == new Vector3(0, 0, 0)) continue;

						// The same model is usually placed many times over, so the section name carries a counter:
						// a repeated header would leave every reader but the last copy behind.
						raw.AppendFormat("[{0}_{1}]\r\n", GetModelName(prop), rawCounter);
						raw.AppendFormat("pos = {0}, {1}, {2}\r\n", Float(prop.Position.X), Float(prop.Position.Y), Float(prop.Position.Z));
						raw.AppendFormat("angle = {0}\r\n\r\n", Float(prop.Rotation.Z));
						rawCounter++;
					}
					File.WriteAllText(path, raw.ToString());
					break;
                case Format.Menyoo:
                    XmlSerializer menSer = new XmlSerializer(typeof(MenyooCompatibility.SpoonerPlacements));
                    map.Objects.RemoveAll(mo => mo.Position == new Vector3(0, 0, 0));
                    var menObj = new MenyooCompatibility.SpoonerPlacements();

			        foreach (var o in map.Objects)
			        {
                        var pl = new MenyooCompatibility.Placement();
                        pl.Type = o.Type == ObjectTypes.Ped ? 1 : o.Type == ObjectTypes.Vehicle ? 2 : 3;
                        pl.Dynamic = o.Dynamic;
			            pl.ModelHash = "0x" + o.Hash.ToString("x8");
                        pl.PositionRotation = new MenyooCompatibility.PositionRotation()
                        {
                            X = o.Position.X,
                            Y = o.Position.Y,
                            Z = o.Position.Z,
                            Pitch = o.Rotation.X,
                            Roll = o.Rotation.Y,
                            Yaw = o.Rotation.Z,
                        };
                        menObj.Placement.Add(pl);
			        }

                    var menF = new StreamWriter(path);
                    menSer.Serialize(menF, menObj);
                    menF.Close();
                    break;
            }
		}

		/// <summary>
		/// A float as both formats want to read it: never in the current culture's decimal comma, never in
		/// scientific notation, and always with the fractional part and the suffix that make it a C# literal.
		/// </summary>
		private static string Float(float value)
		{
			return value.ToString("0.0####", CultureInfo.InvariantCulture) + "f";
		}

		private static string Vec(Vector3 value)
		{
			return string.Format("new Vector3({0}, {1}, {2})", Float(value.X), Float(value.Y), Float(value.Z));
		}

		private static string Bool(bool value)
		{
			return value ? "true" : "false";
		}

		/// <summary>The model's name, falling back to its hash for a model no list knows.</summary>
		private static string GetModelName(MapObject obj)
		{
			if (obj.Type == ObjectTypes.Pickup)
			{
				return Enum.IsDefined(typeof (ObjectDatabase.PickupHash), obj.Hash)
					? ((ObjectDatabase.PickupHash) obj.Hash).ToString()
					: obj.Hash.ToString(CultureInfo.InvariantCulture);
			}

			return ObjectDatabase.NameFor(obj.Type, obj.Hash) ?? obj.Hash.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>A weapon named where SHVDN knows the name, cast from the raw hash where it does not.</summary>
		private static string WeaponLiteral(WeaponHash weapon)
		{
			return Enum.IsDefined(typeof (WeaponHash), weapon)
				? "WeaponHash." + weapon
				: string.Format("(WeaponHash)0x{0:X}u", (uint) weapon);
		}

		/// <summary>
		/// The idle action a ped was given in the editor, as the calls that reproduce it. The scenario names the
		/// editor shows are its own labels; the game only knows the strings <see cref="ObjectDatabase.ScrenarioDatabase"/>
		/// maps them to.
		/// </summary>
		private static IEnumerable<string> ScenarioLines(string action)
		{
			if (string.IsNullOrEmpty(action) || action == "None")
				yield break;

			switch (action)
			{
				case "Any":
				case "Any - Walk":
					yield return "Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped, ped.Position.X, ped.Position.Y, ped.Position.Z, 100f, -1);";
					yield break;
				case "Any - Warp":
					yield return "Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD_WARP, ped, ped.Position.X, ped.Position.Y, ped.Position.Z, 100f, -1);";
					yield break;
				case "Wander":
					yield return "ped.Task.WanderAround();";
					yield break;
			}

			string scenario;
			if (ObjectDatabase.ScrenarioDatabase.TryGetValue(action, out scenario))
				yield return string.Format("Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, ped, \"{0}\", 0, 0);", scenario);
		}
	}
}