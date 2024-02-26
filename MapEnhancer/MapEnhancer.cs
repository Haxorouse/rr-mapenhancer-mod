using Core;
using GalaSoft.MvvmLight.Messaging;
using Game;
using Game.AccessControl;
using Game.Events;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Helpers;
using Map.Runtime;
using MapEnhancer.UMM;
using Model;
using Model.Definition;
using Model.OpsNew;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using TMPro;
using Track;
using Track.Signals;
using UI;
using UI.CarInspector;
using UI.Map;
using UnityEngine;
using UnityEngine.UI;

namespace MapEnhancer;

public class MapEnhancer : MonoBehaviour
{
	public enum MapStates { MAINMENU, MAPLOADED, MAPUNLOADING }
	public static MapStates MapState { get; private set; } = MapStates.MAINMENU;
	internal Loader.MapEnhancerSettings Settings;
	public GameObject Junctions;
	public GameObject JunctionsBranch;
	public GameObject JunctionsMainline;
	private List<Entry> junctionMarkers = new List<Entry>();
	private CullingGroup cullingGroup;
	private BoundingSphere[] cullingSpheres;

	// Holder stops "prefab" from going active immediately
	private static GameObject _prefabHolder;
	internal static GameObject prefabHolder
	{
		get
		{
			if (_prefabHolder == null)
			{
				_prefabHolder = new GameObject("Prefab Holder");
				_prefabHolder.hideFlags = HideFlags.HideAndDontSave;
				_prefabHolder.SetActive(false);
			}
			return _prefabHolder;
		}
	}

	private static MapIcon _traincarPrefab;
	public static MapIcon? traincarPrefab
	{
		get
		{
			if (_traincarPrefab == null) CreateTraincarPrefab();
			return _traincarPrefab;
		}
	}

	private static MapIcon _flarePrefab;
	public static MapIcon? flarePrefab
	{
		get
		{
			if (_flarePrefab == null) CreateFlarePrefab();
			return _flarePrefab;
		}
	}

	private Coroutine traincarColorUpdater;

	private static HashSet<string> _mainlineSegments;
	public static HashSet<string> mainlineSegments
	{
		get
		{
			if (_mainlineSegments == null)
				populateSegmentsAndSwitches();

			return _mainlineSegments!;
		}
	}

	private static HashSet<string> _mainlineSwitches;
	public static HashSet<string> mainlineSwitches
	{
		get
		{
			if (_mainlineSwitches == null)
				populateSegmentsAndSwitches();

			return _mainlineSwitches!;
		}
	}

	private static void populateSegmentsAndSwitches()
	{
		_mainlineSegments = new HashSet<string>();
		_mainlineSwitches = new HashSet<string>();
		foreach (var span in FindObjectsOfType<CTCBlock>(true).SelectMany(block => block.Spans))
		{
			span.UpdateCachedPointsIfNeeded();
			foreach (var seg in span._cachedSegments)
			{
				_mainlineSegments.Add(seg.id);
				_mainlineSwitches.Add(seg.a.id);
				_mainlineSwitches.Add(seg.b.id);
			}
		}
	}

	public static MapEnhancer Instance
	{
		get { return Loader.Instance; }
	}

	void Start()
	{
		Messenger.Default.Register<MapDidLoadEvent>(this, new Action<MapDidLoadEvent>(this.OnMapDidLoad));
		Messenger.Default.Register<MapWillUnloadEvent>(this, new Action<MapWillUnloadEvent>(this.OnMapWillUnload));

		if (StateManager.Shared.Storage != null)
		{
			OnMapDidLoad(new MapDidLoadEvent());
		}
	}

	void OnDestroy()
	{
		Loader.LogDebug("OnDestroy");

		if (JunctionMarker.matJunctionGreen != null) Destroy(JunctionMarker.matJunctionGreen);
		if (JunctionMarker.matJunctionRed != null) Destroy(JunctionMarker.matJunctionRed);

		if (prefabHolder != null)
		{
			//TODO cleanup sprite/tex
			DestroyImmediate(prefabHolder);
		}

		Messenger.Default.Unregister<MapDidLoadEvent>(this);
		Messenger.Default.Unregister<MapWillUnloadEvent>(this);

		if (MapState == MapStates.MAPLOADED)
		{
			OnMapWillUnload(new MapWillUnloadEvent());
			if (MapWindow.instance._window.IsShown) MapWindow.instance.mapBuilder.Rebuild();

			if (_traincarPrefab != null) Destroy(_traincarPrefab);
			_traincarPrefab = null;

			DestroyTraincarMarkers();
			DestroyFlareMarkers();
		}
		MapState = MapStates.MAINMENU;
	}

	private void OnMapDidLoad(MapDidLoadEvent evt)
	{
		Loader.LogDebug("OnMapDidLoad");
		if (MapState == MapStates.MAPLOADED) return;
		Loader.LogDebug("OnMapDidLoad2");

		MapState = MapStates.MAPLOADED;
		JunctionMarker.CreatePrefab();

		Junctions = new GameObject("Junctions");
		Junctions.SetActive(MapWindow.instance._window.IsShown);
		JunctionsMainline = new GameObject("Mainline Junctions");
		JunctionsMainline.transform.SetParent(Junctions.transform, false);
		JunctionsBranch = new GameObject("Branch Junctions");
		JunctionsBranch.transform.SetParent(Junctions.transform, false);
		Junctions.SetActive(MapWindow.instance._window.IsShown);

		MapWindow.instance._window.OnShownDidChange += OnMapWindowShown;

		GatherTraincarMarkers();
		GatherFlareMarkers();

		Messenger.Default.Register<WorldDidMoveEvent>(this, new Action<WorldDidMoveEvent>(this.WorldDidMove));
		var worldPos = WorldTransformer.GameToWorld(new Vector3(0, 0, 0));
		Junctions.transform.position = worldPos;

		Rebuild();
		OnSettingsChanged();
	}

	private void OnMapWillUnload(MapWillUnloadEvent evt)
	{
		Loader.LogDebug("OnMapWillUnload");

		MapState = MapStates.MAPUNLOADING;
		Messenger.Default.Unregister<WorldDidMoveEvent>(this);
		if (cullingGroup != null)
		{
			cullingGroup.Dispose();
		}
		cullingGroup = null;

		if (Junctions != null) Destroy(Junctions);
		junctionMarkers.Clear();

		if (traincarColorUpdater != null) StopCoroutine(traincarColorUpdater);

		MapWindow.instance._window.OnShownDidChange -= OnMapWindowShown;
	}

	private void WorldDidMove(WorldDidMoveEvent evt)
	{
		Loader.LogDebug("WorldDidMove");

		var worldPos = WorldTransformer.GameToWorld(new Vector3(0, 0, 0));
		Junctions.transform.position = worldPos;
		UpdateCullingSpheres();
	}

	private void OnMapWindowShown(bool shown)
	{
		Junctions?.SetActive(shown);

		if (shown)
		{
			traincarColorUpdater = StartCoroutine(TraincarColorUpdater());
		}
		else
		{
			if (traincarColorUpdater != null) StopCoroutine(traincarColorUpdater);
			traincarColorUpdater = null;
		}
	}

	private IEnumerator TraincarColorUpdater()
	{
		WaitForSecondsRealtime wait = new WaitForSecondsRealtime(1f);
		for (;;)
		{
			foreach (var marker in MapBuilder.Shared._mapIcons)
			{
				if (marker == null || !marker.isActiveAndEnabled) continue;

				Car car = marker.transform.parent.GetComponent<Car>();
				if (car == null) continue;

				if (car.Archetype.IsFreight())
				{
					string text;
					bool flag;
					Vector3 vector;
					OpsCarPosition opsCarPosition;
					OpsController opsController = OpsController.Shared;
					if (opsController != null && opsController.TryGetDestinationInfo(car, out text, out flag, out vector, out opsCarPosition))
					{
						Area area = opsController.AreaForCarPosition(opsCarPosition);

						Color color = area ? area.tagColor : Color.gray;

						if (!flag)
						{
							var intensity = 1 / color.maxColorComponent;
							color *= intensity;
						}
						marker.GetComponentInChildren<Image>().color = color;
					}
				}

			}
			yield return wait;
		}
		yield break;
	}

	public void Rebuild()
	{
		Loader.LogDebug("Rebuild");
		if (cullingGroup != null)
		{
			cullingGroup.Dispose();
		}

		CreateSwitches();

		Camera mapCamera = MapBuilder.Shared.mapCamera;
		cullingGroup = new CullingGroup();
		cullingGroup.targetCamera = mapCamera;
		cullingGroup.SetBoundingSphereCount(0);
		cullingGroup.SetBoundingSpheres(cullingSpheres);
		cullingGroup.onStateChanged = new CullingGroup.StateChanged(CullingGroupStateChanged);
		cullingGroup.SetBoundingDistances(new float[] { float.PositiveInfinity });
		cullingGroup.SetDistanceReferencePoint(mapCamera.transform);
		cullingSpheres = new BoundingSphere[junctionMarkers.Count];
		UpdateCullingSpheres();
		cullingGroup.SetBoundingSpheres(cullingSpheres);
		cullingGroup.SetBoundingSphereCount(cullingSpheres.Length);
	}

	private void CullingGroupStateChanged(CullingGroupEvent sphere)
	{
		int index = sphere.index;

		var sd = junctionMarkers[index].SwitchDescriptor;

		if (sphere.isVisible && !sphere.wasVisible)
		{
			junctionMarkers[index].JunctionMarker.SetActive(true);
		}
		else if (!sphere.isVisible && sphere.wasVisible)
		{
			junctionMarkers[index].JunctionMarker.SetActive(false);
		}
	}

	private void UpdateCullingSpheres()
	{
		for (int i = 0; i < TrackObjectManager.Instance._descriptors.switches.Count; i++)
		{
			var geo = junctionMarkers[i].SwitchDescriptor.geometry;
			Vector3 vector = WorldTransformer.GameToWorld(geo.switchHome);
			this.cullingSpheres[i] = new BoundingSphere(vector, 1f);
		}
	}

	private void CreateSwitches()
	{
		Loader.LogDebug("CreateSwitches");
		//foreach (var jm in Junctions.GetComponentsInChildren<JunctionMarker>()) Destroy(jm.transform.parent.gameObject);
		foreach (var jm in junctionMarkers) Destroy(jm.JunctionMarker);
		junctionMarkers.Clear();
		foreach (var kvp in TrackObjectManager.Instance._descriptors.switches)
		{
			var sd = kvp.Value;
			TrackNode node = sd.node;

			var junctionMarker = new GameObject($"JunctionMarker ({node.id})");
			junctionMarker.SetActive(false);
			if (mainlineSwitches.Contains(node.id))
				junctionMarker.transform.SetParent(JunctionsMainline.transform, false);
			else
				junctionMarker.transform.SetParent(JunctionsBranch.transform, false);
			junctionMarkers.Add(new Entry(sd, junctionMarker));
			junctionMarker.transform.localPosition = sd.geometry.switchHome + Vector3.up * 50f;
			junctionMarker.transform.localRotation = sd.geometry.aPointRail.Points.First().Rotation;
			JunctionMarker jm = sd.geometry.aPointRail.hand == Hand.Right ?
				JunctionMarker.junctionMarkerPrefabL :
				JunctionMarker.junctionMarkerPrefabR;

			jm = GameObject.Instantiate(jm, junctionMarker.transform);
			jm.junction = node;
		}
	}

	public void OnSettingsChanged()
	{
		if (MapState != MapStates.MAPLOADED) return;

		foreach (var junctionMarker in Junctions.GetComponentsInChildren<CanvasRenderer>(true))
		{
			var rt = junctionMarker.GetComponent<RectTransform>();
			if (rt != null)
			{
				rt.anchoredPosition = new Vector2(Mathf.Sign(rt.anchoredPosition.x) * (Settings.MarkerScale * 40f + 8f), 0f);
				rt.localScale = new Vector3(Settings.MarkerScale * 2f, Settings.MarkerScale, Settings.MarkerScale * 2f);
			}
		}

		foreach (var flare in FlareManager.Shared._instances.Values)
		{
			var icon = flare.GetComponentInChildren<Image>();
			if (icon != null)
			{
				icon.transform.localScale = new Vector3(Settings.FlareScale, Settings.FlareScale, Settings.FlareScale);
			}
		}

		MapBuilder.Shared.segmentLineWidthMin = Settings.TrackLineThickness;
		MapBuilder.Shared.segmentLineWidthMax = Settings.MapZoomMax / 5000f * 20;

		if (MapWindow.instance._window.IsShown)
		{
			MapBuilder.Shared.mapCamera.orthographicSize =
				Mathf.Clamp(MapBuilder.Shared.mapCamera.orthographicSize, Settings.MapZoomMin, Settings.MapZoomMax);
			MapBuilder.Shared.UpdateForZoom();
			MapWindow.instance.mapBuilder.Rebuild();
			OnMapWindowShown(true);
		}
	}

	private static void CreateTraincarPrefab()
	{
		var sprite = LoadTexture("traincar.png", "MapTraincarIcon");
		foreach (var mapIcon in Resources.FindObjectsOfTypeAll<MapIcon>().Where(MapIcon => MapIcon.name.StartsWith("Map Icon Locomotive")))
		{
			mapIcon.transform.Find("Image").localScale = new Vector3(0.8f, 0.8f, 0.8f);
			mapIcon.transform.Find("Text").transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
		}
		_traincarPrefab = Instantiate<MapIcon>(TrainController.Shared.locomotiveMapIconPrefab, prefabHolder.transform);
		GameObject trainCarMarker = _traincarPrefab.gameObject;
		trainCarMarker.hideFlags = HideFlags.HideAndDontSave;
		trainCarMarker.name = "Map Icon Traincar";
		_traincarPrefab.GetComponentInChildren<Image>().sprite = sprite;

		trainCarMarker.AddComponent<CanvasCuller>();
	}

	private static void CreateFlarePrefab()
	{
		var scale = Instance?.Settings.FlareScale ?? 0.6f;
		var sprite = LoadTexture("flare.png", "MapFlareIcon");
		_flarePrefab = Instantiate<MapIcon>(TrainController.Shared.locomotiveMapIconPrefab, prefabHolder.transform);
		GameObject flareMarker = _flarePrefab.gameObject;
		flareMarker.hideFlags = HideFlags.HideAndDontSave;
		flareMarker.name = "Map Icon Flare";
		if (_flarePrefab.Text) Destroy(_flarePrefab.Text);
		var image = _flarePrefab.GetComponentInChildren<Image>();
		image.sprite = sprite;
		image.transform.localScale = new Vector3(scale, scale, scale);
	}

	private static Sprite? LoadTexture(string fileName, string name)
	{
		string iconPath = Path.Combine(Loader.ModEntry.Path, fileName);
		var tex = new Texture2D(128, 128, TextureFormat.DXT5, false);
		tex.name = name;
		tex.wrapMode = TextureWrapMode.Clamp;
		if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(iconPath)))
		{
			Loader.Log("Unable to load traincar icon!");
			return null;
		}
		Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
		sprite.name = name;
		return sprite;
	}

	private void GatherTraincarMarkers()
	{
		foreach (Car car in TrainController.Shared.Cars)
		{
			if (!car.Archetype.IsLocomotive())
			{
				var marker = car.GetComponentInChildren<MapIcon>();
				if (!marker)
					AddTraincarMarker(car);
			}
		}
	}

	private void DestroyTraincarMarkers()
	{
		foreach (Car car in TrainController.Shared.Cars)
		{
			if (!car.Archetype.IsLocomotive())
			{
				var marker = car.GetComponentInChildren<MapIcon>();
				if (marker)
					DestroyImmediate(marker.gameObject);
			}
		}
	}

	internal static void AddTraincarMarker(Car car)
	{
		car.MapIcon = Instantiate<MapIcon>(traincarPrefab, car.transform);
		if (car.Archetype == CarArchetype.Tender)
			car.MapIcon.SetText(car.Ident.RoadNumber);
		else
			car.MapIcon.SetText($"<line-height=70%>{car.Ident.ReportingMark}\n{car.Ident.RoadNumber}");
		var image = car.MapIcon.GetComponentInChildren<Image>();
		var scale = image.transform.localScale;
		scale.y = (car.carLength / 11) * scale.y;
		image.transform.localScale = scale;
		var text = car.MapIcon.GetComponentInChildren<TMP_Text>();
		text.horizontalAlignment = HorizontalAlignmentOptions.Left;
		text.enableAutoSizing = false;
		text.fontSizeMin = 19;
		text.fontSize = 19;
		text.autoSizeTextContainer = true;
		text.transform.localPosition = Vector3.zero;


		car.MapIcon.OnClick = delegate
		{
			CarInspector.Show(car);
		};
		car.UpdateMapIconPosition(car._mover.Position, car._mover.Rotation);
	}

	private void GatherFlareMarkers()
	{
		foreach (GameObject flareGO in FlareManager.Shared._instances.Values)
		{
			var flare = flareGO.GetComponentInChildren<FlarePickable>();
			var marker = flare.GetComponentInChildren<MapIcon>();
			if (!marker)
				AddFlareMarker(flare);
		}
	}

	private void DestroyFlareMarkers()
	{
		foreach (GameObject flareGO in FlareManager.Shared._instances.Values)
		{
				var marker = flareGO.GetComponentInChildren<MapIcon>();
				if (marker)
					DestroyImmediate(marker.gameObject);
		}
	}

	internal static void AddFlareMarker(FlarePickable flare)
	{
		var mapIcon = Instantiate<MapIcon>(flarePrefab, flare.transform.parent);
		var posRot = flare.transform.parent.parent.GetComponent<TrackMarker>().PositionRotation;
		mapIcon.transform.localPosition = mapIcon.transform.localPosition + Vector3.up * 25f;
		mapIcon.transform.rotation = Quaternion.Euler(90f, posRot.Value.Rotation.eulerAngles.y, 0f);
		mapIcon.OnClick = delegate
		{
			flare.Activate();
		};
	}

	void Update()
	{
		if (MapState != MapStates.MAPLOADED) return;

		var mapWindow = MapWindow.instance;
		var mapDrag = MapWindow.instance.mapDrag;
		if (!MapWindow.instance.mapDrag._pointerOver || !GameInput.IsMouseOverGameWindow(mapWindow._window)) return;

		if (GameInput.shared.PlaceFlare)
		{
			Vector2 viewportNormalizedPoint = mapDrag.NormalizedMousePosition();
			Ray ray = mapWindow.RayForViewportNormalizedPoint(viewportNormalizedPoint);
			Vector3 vector = MapManager.Instance.FindTerrainPointForXZ(WorldTransformer.WorldToGame(ray.origin));
			Location? location = LocationFromGamePoint(vector, 50f);
			if (location != null)
			{
				StateManager.ApplyLocal(new FlareAddUpdate(Graph.CreateSnapshotTrackLocation(location.Value)));
			}
		}
	}

	public Location? LocationFromGamePoint(Vector3 gamePosition, float radius)
	{
		List<Location> locations = new List<Location>();
		foreach (TrackSegment trackSegment in Graph.Shared.segments.Values)
		{
			//if (Vector3.Magnitude(trackSegment.Curve.EndPoint1 - gamePosition) > 1000f) continue;

			Location? result = Graph.Shared.LocationFromPoint(trackSegment, gamePosition, radius);
			if (result.HasValue && result.Value.IsValid)
			{
				locations.Add((Location)result);
			}
		}

		if (locations.Count > 0)
			return locations.OrderBy(a => Vector3.Magnitude(a.GetPosition() - gamePosition)).First();

		return null;
	}

	private class Entry
	{
		public Entry(TrackObjectManager.SwitchDescriptor switchDescriptor, GameObject junctionMarker)
		{
			SwitchDescriptor = switchDescriptor;
			JunctionMarker = junctionMarker;
		}

		public readonly TrackObjectManager.SwitchDescriptor SwitchDescriptor;
		public readonly GameObject JunctionMarker;
	}

	[HarmonyPatch(typeof(TrackObjectManager), nameof(TrackObjectManager.Rebuild))]
	private static class TrackObjectManagerRebuildPatch
	{
		private static void Postfix()
		{
			if (MapState != MapStates.MAPLOADED) return;

			Instance?.Rebuild();

			if (MapWindow.instance._window.IsShown) MapWindow.instance.mapBuilder.Rebuild();
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.TrackColorMainline), MethodType.Getter)]
	private static class TrackColorMainlinePatch
	{
		private static bool Prefix(ref Color __result)
		{
			__result = Loader.Settings.TrackColorMainline;

			return false;
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.TrackColorBranch), MethodType.Getter)]
	private static class TrackColorBranchPatch
	{
		private static bool Prefix(ref Color __result)
		{
			__result = Loader.Settings.TrackColorBranch;

			return false;
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.TrackColorIndustrial), MethodType.Getter)]
	private static class TrackColorIndustrialPatch
	{
		private static bool Prefix(ref Color __result)
		{
			__result = Loader.Settings.TrackColorIndustrial;

			return false;
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.TrackColorUnavailable), MethodType.Getter)]
	private static class TrackColorUnavailablePatch
	{
		private static bool Prefix(ref Color __result)
		{
			__result = Loader.Settings.TrackColorUnavailable;

			return false;
		}
	}

	[HarmonyPatch(typeof(TrackSegment), nameof(TrackSegment.Awake))]
	private static class SegmentTrackClassPatch
	{
		private static void Postfix(TrackSegment __instance)
		{
			if (mainlineSegments.Contains(__instance.id))
				__instance.trackClass = TrackClass.Mainline;
			else
				__instance.trackClass = TrackClass.Branch;
		}
	}

	/*
	[HarmonyPatch(typeof(PassengerStop), nameof(PassengerStop.OnEnable))]
	private static class PaxTrackClassPatch
	{
		private static void Postfix(PassengerStop __instance)
		{
			foreach (var tspan in __instance.TrackSpans)
			{
				tspan.UpdateCachedPointsIfNeeded();
				foreach (var seg in tspan._cachedSegments)
				{
					seg.trackClass = Track.TrackClass.Industrial;
				}
			}
		}
	}
	*/

	[HarmonyPatch(typeof(IndustryComponent), nameof(IndustryComponent.Start))]
	private static class IndustryTrackClassPatch
	{
		private static void Postfix(IndustryComponent __instance)
		{
			if (__instance is ProgressionIndustryComponent) return;
			foreach (var tspan in __instance.TrackSpans)
			{
				tspan.UpdateCachedPointsIfNeeded();
				foreach (var seg in tspan._cachedSegments)
				{
					seg.trackClass = Track.TrackClass.Industrial;
				}
			}
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.UpdateForZoom))]
	private static class MapBuilderZoomPatch
	{
		private static void Postfix(MapBuilder __instance)
		{
			Instance?.JunctionsBranch?.SetActive(__instance.NormalizedScale <= Loader.Settings.MarkerCutoff);
		}
	}

	[HarmonyPatch(typeof(TrainController), nameof(TrainController.HandleRequestSetSwitch))]
	private static class HostAccessLevelSetSwitchPatch
	{
		private static bool Prefix(TrainController __instance, RequestSetSwitch setSwitch, IPlayer sender)
		{
			TrackNode node = __instance.graph.GetNode(setSwitch.nodeId);
			if (node.IsCTCSwitch && HostManager.Shared.AccessLevelForPlayerId(sender.PlayerId) < AccessLevel.Dispatcher) return false;
			return true;
		}
	}

	[HarmonyPatch(typeof(Car), nameof(Car.FinishSetup))]
	private static class CarFinishSetupPatch
	{
		private static void Postfix(Car __instance)
		{
			AddTraincarMarker(__instance);
		}
	}
	
	[HarmonyPatch(typeof(Car), nameof(Car.UpdateMapIconPosition))]
	private static class CarUpdatePositionPatch
	{
		private static bool Prefix(Car __instance, Vector3 position, Quaternion rotation)
		{
			if (__instance.MapIcon == null)
			{
				return false;
			}
			if (__instance.Archetype.IsLocomotive())
				return true;
			
			__instance.MapIcon.transform.SetPositionAndRotation(position + Vector3.up * 75f, Quaternion.Euler(-90f, rotation.eulerAngles.y, 0f));
			return false;
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.Zoom))]
	public static class ChangeMinMaxMapZoom
	{
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var codeMatcher = new CodeMatcher(instructions)
				.MatchStartForward(
				new CodeMatch(OpCodes.Ldc_R4, 100f),
				new CodeMatch(OpCodes.Ldc_R4, 5000f),
				new CodeMatch(OpCodes.Call))//, ((Func<GameObject, Transform, GameObject>)UnityEngine.Object.Instantiate<GameObject>).Method.GetGenericMethodDefinition()))
				.ThrowIfNotMatch("Could not find Mathf.Clamp.map")
				.SetAndAdvance(OpCodes.Ldsfld, AccessTools.Field(typeof(Loader), nameof(Loader.Settings)))
				.SetAndAdvance(OpCodes.Ldfld, AccessTools.Field(typeof(Loader.MapEnhancerSettings), nameof(Loader.MapEnhancerSettings.MapZoomMin)))
				.InsertAndAdvance(new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Loader), nameof(Loader.Settings))))
				.InsertAndAdvance(new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Loader.MapEnhancerSettings), nameof(Loader.MapEnhancerSettings.MapZoomMax))));
			return codeMatcher.InstructionEnumeration();
		}
	}
	
	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.NormalizedScale), MethodType.Getter)]
	private static class ChangeMinMaxMapZoom2
	{
		private static bool Prefix(Camera ___mapCamera, ref float __result)
		{
			__result = Mathf.InverseLerp(Instance?.Settings.MapZoomMin ?? 100f,
				Instance?.Settings.MapZoomMax ?? 5000f, ___mapCamera.orthographicSize);
			return false;
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.IconScale), MethodType.Getter)]
	private static class ChangeMinMaxMapZoom3
	{
		private static bool Prefix(MapBuilder __instance, ref float __result)
		{
			if (Instance == null) return true;

			var min = Mathf.LerpUnclamped(0.2f, 4f, InverseLerpUnclamped(100f, 5000f, Instance.Settings.MapZoomMin));
			var max = Mathf.LerpUnclamped(0.2f, 4f, InverseLerpUnclamped(100f, 5000f, Instance.Settings.MapZoomMax));
			__result = Mathf.Lerp(min, max, __instance.NormalizedScale); ;
			return false;
		}

		public static float InverseLerpUnclamped(float a, float b, float value)
		{
			if (a != b)
			{
				return (value - a) / (b - a);
			}

			return 0f;
		}
	}

	[HarmonyPatch(typeof(MapLabel), nameof(MapLabel.SetZoom))]
	private static class ChangeMinMaxMapZoom4
	{
		private static bool Prefix(ref float s)
		{
			s = s / 4f * 3.5f;
			return true;
		}
	}
	
	[HarmonyPatch]
	public static class PreventRebuildFromMovingCamera
	{
		[HarmonyTranspiler]
		[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.Rebuild))]
		static IEnumerable<CodeInstruction> RebuildTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var codeMatcher = new CodeMatcher(instructions)
				.MatchStartForward(
				new CodeMatch(OpCodes.Call, AccessTools.PropertyGetter(typeof(CameraSelector), "shared")),
				new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(CameraSelector), "CurrentCameraPosition")),
				new CodeMatch(OpCodes.Stloc_1))
				.ThrowIfNotMatch("Could not find CameraSelector.Shared.get_CurrentCameraPosition()")
				.RemoveInstructionsWithOffsets (0, 12);
			return codeMatcher.InstructionEnumeration();
		}

		[HarmonyTranspiler]
		[HarmonyPatch(typeof(MapWindow), nameof(MapWindow.OnWindowShown))]
		static IEnumerable<CodeInstruction> OnWindowShownTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var codeMatcher = new CodeMatcher(instructions)
				.MatchEndForward(
				new CodeMatch(OpCodes.Ldarg_0),
				new CodeMatch(OpCodes.Ldfld),
				new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(MapBuilder), "Rebuild")))
				.ThrowIfNotMatch("Could not find MapWindow.OnWindowShown()")
				.InsertAndAdvance(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PreventRebuildFromMovingCamera), nameof(PreventRebuildFromMovingCamera.RecenterMap))));
			return codeMatcher.InstructionEnumeration();
		}

		public static void RecenterMap()
		{
			Vector3 currentCameraPosition = CameraSelector.shared.CurrentCameraPosition;
			MapBuilder.Shared.mapCamera.transform.localPosition = new Vector3(currentCameraPosition.x, 5000f, currentCameraPosition.z);
		}
	}

	[HarmonyPatch(typeof(MapBuilder), nameof(MapBuilder.UpdateCullingSpheres))]
	public static class IncreaseBoundingSphereForSplinesPatch
	{
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var codeMatcher = new CodeMatcher(instructions)
				.MatchStartForward(
				new CodeMatch(OpCodes.Ldloc_3),
				new CodeMatch(OpCodes.Ldc_R4, 1f))
				//new CodeMatch(OpCodes.Newobj))//, ((Func<GameObject, Transform, GameObject>)UnityEngine.Object.Instantiate<GameObject>).Method.GetGenericMethodDefinition()))
				.ThrowIfNotMatch("Could not find new BoundingSphere")
				.Advance(1)
				.Set(OpCodes.Ldc_R4, 100f);
			return codeMatcher.InstructionEnumeration();
		}
	}
	
	[HarmonyPatch(typeof(FlarePickable), nameof(FlarePickable.Configure))]
	private static class FlareAddUpdatePatch
	{
		private static void Postfix(FlarePickable __instance)
		{
			AddFlareMarker(__instance);
		}
	}

	[HarmonyPatch(typeof(FlareManager), nameof(FlareManager.PlaceFlare))]
	public static class PlaceFlareProtectionPatch
	{
		private static bool Prefix(Camera theCamera)
		{
			if (!GameInput.IsMouseOverGameWindow() || theCamera == null)
			{
				return false;
			}
			return true;
		}
	}
}
