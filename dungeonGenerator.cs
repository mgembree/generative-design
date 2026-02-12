using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class dungeonGenerator : MonoBehaviour
{
	[Header("Data")]
	[SerializeField] private TextAsset ecosystemJson;

	[Header("Generation")]
	[SerializeField] private int roomMin = 8;
	[SerializeField] private int roomMax = 14;
	[SerializeField] private int layoutsToGenerate = 5;
	[SerializeField] private bool generateOnStart = true;
	[SerializeField] private string biomeToBeGenerated = "forest";
	[SerializeField] private float creatureCrWeightExponent = 1.0f;
	[Header("Optional 2D Visualization")]
	[SerializeField] private bool spawnRoomObjects = false;
	[SerializeField] private GameObject roomPrefab;
	[SerializeField] private Transform roomsParent;
	[SerializeField] private bool spawnEdges = true;
	[SerializeField] private Transform edgesParent;
	[Header("Noise Settings")]
	[SerializeField] private float noiseScale = 0.18f;
	[SerializeField] private float noiseJitter = 0.5f;
	[SerializeField] private float noiseMinSpacing = 0.9f;
	[SerializeField] private int noiseCandidateMultiplier = 6;
	[SerializeField] private int noiseOctaves = 4;
	[SerializeField] private float noiseLacunarity = 2.0f;
	[SerializeField] private float noiseGain = 0.5f;

	private float roomSpacing = 1.6f;
	private Vector2 defaultRoomSizeRange = new Vector2(1f, 1.5f);
	private BiomeRoomSize[] biomeRoomSizes;
	private float roomScaleMultiplier = 0.8f;
	private bool showRoomLabels = true;
	private int labelFontSize = 9;
	private float labelYOffset = 0.6f;
	private Color labelColor = Color.red;
	private float edgeWidth = 0.05f;
	private Color edgeColor = Color.red;
	private int overlapIterations = 40;
	private float overlapPadding = 0.18f;
	private bool showRegenerateButton = true;
	private string regenerateButtonText = "Regenerate";
	private Vector2 regenerateButtonPosition = new Vector2(16f, 16f);
	private Vector2 regenerateButtonSize = new Vector2(140f, 40f);
	private bool showBiomeLabel = true;
	private Vector2 biomeLabelPosition = new Vector2(16f, 64f);
	private Vector2 biomeLabelSize = new Vector2(240f, 24f);
	private bool showRoomInfoPanel = true;
	private Vector2 roomInfoPosition = new Vector2(16f, 96f);
	private Vector2 roomInfoSize = new Vector2(320f, 140f);
	[TextArea(6, 20)] private string lastOutput;

	private readonly List<GameObject> spawnedRooms = new List<GameObject>();
	private readonly List<GameObject> spawnedEdges = new List<GameObject>();
	private RoomInfo selectedRoom;
	private string currentBiome = "";

	private void Start()
	{
		currentBiome = biomeToBeGenerated;
		if (generateOnStart)
		{
			GenerateEcosystem();
		}
	}

	private void Update()
	{
		HandleRoomClick();
	}

	private void OnGUI()
	{
		if (!showRegenerateButton)
		{
			return;
		}

		var rect = new Rect(regenerateButtonPosition, regenerateButtonSize);
		if (GUI.Button(rect, regenerateButtonText))
		{
			GenerateEcosystem();
		}

		if (showBiomeLabel)
		{
			var biomeRect = new Rect(biomeLabelPosition, biomeLabelSize);
			GUI.Label(biomeRect, $"Biome: {currentBiome}");
		}

		if (showRoomInfoPanel)
		{
			var infoRect = new Rect(roomInfoPosition, roomInfoSize);
			var text = selectedRoom == null
				? "Click a room to see creatures."
				: $"Room: {selectedRoom.label}\nSize: {selectedRoom.size}\nCreatures: {(selectedRoom.occupants != null && selectedRoom.occupants.Length > 0 ? string.Join(", ", selectedRoom.occupants) : "None")}\nNeighbors: {(selectedRoom.neighbors != null && selectedRoom.neighbors.Length > 0 ? string.Join(", ", selectedRoom.neighbors) : "None")}";
			GUI.Box(infoRect, text);
		}
	}

	[ContextMenu("Generate Ecosystem")]
	public void GenerateEcosystem()
	{
		var ecosystem = LoadEcosystem();
		if (ecosystem == null)
		{
			Debug.LogError("Ecosystem JSON not set or failed to parse.");
			return;
		}

		var output = new StringBuilder();
		ClearSpawnedRooms();
		ClearSpawnedEdges();
		currentBiome = biomeToBeGenerated;

		for (var i = 0; i < Mathf.Max(1, layoutsToGenerate); i++)
		{
			var roomCount = GetRoomCount();
			var positions = GenerateNoisePositions(roomCount);
			var graph = new Graph
			{
				nodes = CreateNodes(roomCount),
				adjacency = BuildAdjacencyByNearest(positions, roomCount, 2)
			};
			EnsureConnected(graph.adjacency, positions, 0, roomCount - 1);
			var assignment = AssignRooms(graph, ecosystem, biomeToBeGenerated);
			if (i == 0)
			{
				currentBiome = assignment.biome;
			}
			var text = $"Layout {i + 1}:\n{RenderRooms(graph, assignment.biome, assignment.rooms)}";

			output.AppendLine(text);
			output.AppendLine();

			if (spawnRoomObjects && i == 0)
			{
				currentBiome = assignment.biome;
				var maxDiameter = ComputeMaxRoomDiameter(positions);
				var scales = ComputeRoomScales(assignment.rooms, maxDiameter);
				var baseSize = GetPrefabBaseSize();
				ResolveOverlaps(positions, scales, baseSize);
				SpawnRooms(assignment.rooms, graph.adjacency, positions, scales);
				if (spawnEdges)
				{
					SpawnEdges(assignment.rooms, graph.adjacency, positions);
				}
			}
		}

		lastOutput = output.ToString().TrimEnd();
		Debug.Log(lastOutput);
	}

	private void HandleRoomClick()
	{
		if (!spawnRoomObjects || !GetMouseClickDown())
		{
			return;
		}

		var cam = Camera.main;
		if (cam == null)
		{
			return;
		}

		var mousePos = GetMousePosition();
		var world = cam.ScreenToWorldPoint(mousePos);
		var hit2D = Physics2D.Raycast(world, Vector2.zero);
		if (hit2D.collider != null)
		{
			selectedRoom = hit2D.collider.GetComponentInParent<RoomInfo>();
			return;
		}

		var ray = cam.ScreenPointToRay(mousePos);
		if (Physics.Raycast(ray, out var hit3D))
		{
			selectedRoom = hit3D.collider.GetComponentInParent<RoomInfo>();
		}
	}

	private static bool GetMouseClickDown()
	{
#if ENABLE_INPUT_SYSTEM
		return UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame;
#else
		return Input.GetMouseButtonDown(0);
#endif
	}

	private static Vector3 GetMousePosition()
	{
#if ENABLE_INPUT_SYSTEM
		return UnityEngine.InputSystem.Mouse.current != null
			? (Vector3)UnityEngine.InputSystem.Mouse.current.position.ReadValue()
			: Vector3.zero;
#else
		return Input.mousePosition;
#endif
	}

	private EcosystemData LoadEcosystem()
	{
		if (ecosystemJson == null)
		{
			return null;
		}

		try
		{
			return JsonUtility.FromJson<EcosystemData>(ecosystemJson.text);
		}
		catch (Exception ex)
		{
			Debug.LogError($"Failed to parse ecosystem JSON: {ex.Message}");
			return null;
		}
	}

	private int GetRoomCount()
	{
		var min = Mathf.Max(2, roomMin);
		var max = Mathf.Max(2, roomMax);
		if (min > max)
		{
			var tmp = min;
			min = max;
			max = tmp;
		}

		return RandInt(min, max);
	}

	private List<RoomNode> CreateNodes(int roomCount)
	{
		var nodes = new List<RoomNode>(roomCount);
		for (var i = 0; i < roomCount; i++)
		{
			var label = i == 0 ? "START" : (i == roomCount - 1 ? "GOAL" : $"R{i}");
			nodes.Add(new RoomNode { id = i, label = label });
		}
		return nodes;
	}


	private Vector3[] GenerateNoisePositions(int count)
	{
		var positions = new Vector3[count];
		if (count == 0)
		{
			return positions;
		}

		var area = new Vector2(roomSpacing * count, roomSpacing * count);
		area.x = Mathf.Max(4f, area.x);
		area.y = Mathf.Max(4f, area.y);

		var candidateCount = Mathf.Max(count * noiseCandidateMultiplier, count + 8);
		var candidates = new List<(Vector3 pos, float score)>(candidateCount);
		var noiseOffset = UnityEngine.Random.insideUnitCircle * 1000f;
		for (var i = 0; i < candidateCount; i++)
		{
			var u = UnityEngine.Random.value;
			var v = UnityEngine.Random.value;
			var x = Mathf.Lerp(-area.x * 0.5f, area.x * 0.5f, u);
			var y = Mathf.Lerp(-area.y * 0.5f, area.y * 0.5f, v);
			var n = FractalNoise((x + noiseOffset.x) * noiseScale, (y + noiseOffset.y) * noiseScale);
			var score = n + UnityEngine.Random.Range(-noiseJitter, noiseJitter) * 0.1f;
			candidates.Add((new Vector3(x, y, 0f), score));
		}

		candidates.Sort((a, b) => b.score.CompareTo(a.score));
		var minSpacing = Mathf.Max(0.5f, noiseMinSpacing);
		var picked = new List<Vector3>(count);
		var candidateIndex = 0;
		while (picked.Count < count && candidateIndex < candidates.Count)
		{
			var candidate = candidates[candidateIndex].pos;
			candidateIndex++;
			if (IsFarEnough(candidate, picked, minSpacing))
			{
				picked.Add(candidate);
			}
		}

		while (picked.Count < count)
		{
			var fallback = new Vector3(
				UnityEngine.Random.Range(-area.x * 0.5f, area.x * 0.5f),
				UnityEngine.Random.Range(-area.y * 0.5f, area.y * 0.5f),
				0f);
			picked.Add(fallback);
		}

		for (var i = 0; i < count; i++)
		{
			positions[i] = picked[i];
		}

		return positions;
	}

	private float FractalNoise(float x, float y)
	{
		var amplitude = 0.5f;
		var frequency = 1f;
		var sum = 0f;
		var max = 0f;
		var octaves = Mathf.Max(1, noiseOctaves);
		for (var i = 0; i < octaves; i++)
		{
			sum += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
			max += amplitude;
			frequency *= Mathf.Max(1f, noiseLacunarity);
			amplitude *= Mathf.Clamp01(noiseGain);
		}

		return max > 0f ? sum / max : sum;
	}

	private static bool IsFarEnough(Vector3 candidate, List<Vector3> positions, float minDistance)
	{
		var minSq = minDistance * minDistance;
		for (var i = 0; i < positions.Count; i++)
		{
			if ((positions[i] - candidate).sqrMagnitude < minSq)
			{
				return false;
			}
		}
		return true;
	}

	private static List<HashSet<int>> BuildAdjacencyByNearest(Vector3[] positions, int nodeCount, int neighborsPerNode)
	{
		var adjacency = new List<HashSet<int>>(nodeCount);
		for (var i = 0; i < nodeCount; i++)
		{
			adjacency.Add(new HashSet<int>());
		}

		if (positions == null || positions.Length < nodeCount || nodeCount <= 1)
		{
			return adjacency;
		}

		var k = Mathf.Clamp(neighborsPerNode, 0, Mathf.Max(0, nodeCount - 1));
		for (var i = 0; i < nodeCount; i++)
		{
			var list = new List<(int id, float dist)>();
			for (var j = 0; j < nodeCount; j++)
			{
				if (i == j)
				{
					continue;
				}
				var dist = (positions[i] - positions[j]).sqrMagnitude;
				list.Add((j, dist));
			}

			list.Sort((a, b) => a.dist.CompareTo(b.dist));
			for (var n = 0; n < k && n < list.Count; n++)
			{
				var other = list[n].id;
				adjacency[i].Add(other);
				adjacency[other].Add(i);
			}
		}

		return adjacency;
	}

	private static void EnsureConnected(List<HashSet<int>> adjacency, Vector3[] positions, int startId, int goalId)
	{
		if (adjacency == null || positions == null || adjacency.Count == 0 || positions.Length < adjacency.Count)
		{
			return;
		}

		if (IsValidNode(startId, adjacency.Count) && IsValidNode(goalId, adjacency.Count))
		{
			adjacency[startId].Remove(goalId);
			adjacency[goalId].Remove(startId);
		}

		var components = GetComponents(adjacency);
		if (components.Count <= 1)
		{
			return;
		}

		// Connect components by linking the closest pair of nodes between components.
		while (components.Count > 1)
		{
			var bestA = -1;
			var bestB = -1;
			var bestDist = float.PositiveInfinity;

			for (var c = 0; c < components.Count; c++)
			{
				for (var d = c + 1; d < components.Count; d++)
				{
					var compA = components[c];
					var compB = components[d];
					for (var i = 0; i < compA.Count; i++)
					{
						var nodeA = compA[i];
						for (var j = 0; j < compB.Count; j++)
						{
							var nodeB = compB[j];
							if (IsForbiddenPair(nodeA, nodeB, startId, goalId))
							{
								continue;
							}
							var dist = (positions[nodeA] - positions[nodeB]).sqrMagnitude;
							if (dist < bestDist)
							{
								bestDist = dist;
								bestA = nodeA;
								bestB = nodeB;
							}
						}
					}
				}
			}

			if (bestA < 0 || bestB < 0)
			{
				break;
			}

			adjacency[bestA].Add(bestB);
			adjacency[bestB].Add(bestA);
			components = GetComponents(adjacency);
		}
	}

	private static bool IsForbiddenPair(int a, int b, int startId, int goalId)
	{
		return (a == startId && b == goalId) || (a == goalId && b == startId);
	}

	private static bool IsValidNode(int id, int count)
	{
		return id >= 0 && id < count;
	}

	private string ResolveBiome(BiomeData[] biomes, string preferredBiome)
	{
		if (!string.IsNullOrWhiteSpace(preferredBiome))
		{
			for (var i = 0; i < biomes.Length; i++)
			{
				if (biomes[i] != null && string.Equals(biomes[i].name, preferredBiome, StringComparison.OrdinalIgnoreCase))
				{
					return biomes[i].name;
				}
			}
		}

		var picked = Pick(biomes);
		return picked != null && !string.IsNullOrWhiteSpace(picked.name) ? picked.name : "unknown";
	}

	private CreatureData PickCreatureWeighted(List<CreatureData> pool)
	{
		if (pool == null || pool.Count == 0)
		{
			return null;
		}

		var exponent = Mathf.Max(0.1f, creatureCrWeightExponent);
		var total = 0f;
		for (var i = 0; i < pool.Count; i++)
		{
			total += GetCreatureWeight(pool[i], exponent);
		}

		if (total <= 0f)
		{
			return pool[RandInt(0, pool.Count - 1)];
		}

		var roll = UnityEngine.Random.value * total;
		for (var i = 0; i < pool.Count; i++)
		{
			roll -= GetCreatureWeight(pool[i], exponent);
			if (roll <= 0f)
			{
				return pool[i];
			}
		}

		return pool[pool.Count - 1];
	}

	private static float GetCreatureWeight(CreatureData creature, float exponent)
	{
		if (creature == null)
		{
			return 0f;
		}

		var cr = Mathf.Max(0.01f, creature.cr);
		return Mathf.Pow(cr, exponent);
	}

	private static List<List<int>> GetComponents(List<HashSet<int>> adjacency)
	{
		var count = adjacency.Count;
		var visited = new bool[count];
		var components = new List<List<int>>();
		for (var i = 0; i < count; i++)
		{
			if (visited[i])
			{
				continue;
			}

			var component = new List<int>();
			var stack = new Stack<int>();
			stack.Push(i);
			visited[i] = true;
			while (stack.Count > 0)
			{
				var node = stack.Pop();
				component.Add(node);
				foreach (var neighbor in adjacency[node])
				{
					if (!visited[neighbor])
					{
						visited[neighbor] = true;
						stack.Push(neighbor);
					}
				}
			}
			components.Add(component);
		}

		return components;
	}


	private RoomAssignment AssignRooms(Graph graph, EcosystemData ecosystem, string preferredBiome)
	{
		if (ecosystem == null || ecosystem.biomes == null || ecosystem.biomes.Length == 0)
		{
			return new RoomAssignment { biome = "unknown", rooms = new List<RoomData>() };
		}

		var biome = ResolveBiome(ecosystem.biomes, preferredBiome);
		var options = new List<CreatureData>();
		if (ecosystem.creatures != null)
		{
			foreach (var creature in ecosystem.creatures)
			{
				if (creature == null || creature.population_range == null || creature.population_range.Length < 2)
				{
					continue;
				}

				if (creature.population_range[1] <= 0)
				{
					continue;
				}

				if (HasBiome(creature, biome))
				{
					options.Add(creature);
				}
			}
		}

		var rooms = new List<RoomData>(graph.nodes.Count);
		foreach (var node in graph.nodes)
		{
			var size = DetermineRoomSize(node, graph.adjacency);
			var maxPick = Mathf.Min(3, options.Count);
			var selectedCount = maxPick > 0 ? RandInt(1, maxPick) : 0;
			var selected = new List<CreatureData>();
			var pool = new List<CreatureData>(options);
			for (var i = 0; i < selectedCount; i++)
			{
				var pick = PickCreatureWeighted(pool);
				if (pick == null)
				{
					break;
				}
				selected.Add(pick);
				pool.Remove(pick);
			}

			var occupants = new List<string>();
			foreach (var creature in selected)
			{
				var minPop = Mathf.Max(1, creature.population_range[0]);
				var maxPop = Mathf.Max(minPop, creature.population_range[1]);
				var pop = RandInt(minPop, maxPop);
				occupants.Add($"{creature.name} x{pop}");
			}

			rooms.Add(new RoomData
			{
				id = node.id,
				label = node.label,
				biome = biome,
				occupants = occupants,
				size = size
			});
		}

		return new RoomAssignment { biome = biome, rooms = rooms };
	}

	private string RenderRooms(Graph graph, string biome, List<RoomData> rooms)
	{
		var lines = new List<string>
		{
			"Rooms and Connections:",
			$"Biome: {biome}"
		};

		foreach (var room in rooms)
		{
			var neighbors = new List<string>();
			foreach (var neighborId in graph.adjacency[room.id])
			{
				neighbors.Add(rooms[neighborId].label);
			}

			neighbors.Sort();

			lines.Add(string.Empty);
			lines.Add(room.label);
			lines.Add($"  Creatures: {(room.occupants.Count > 0 ? string.Join(", ", room.occupants) : "None")}");
			lines.Add($"  Adjacent: {string.Join(", ", neighbors)}");
		}

		return string.Join("\n", lines);
	}

	private void SpawnRooms(List<RoomData> rooms, List<HashSet<int>> adjacency, Vector3[] positions, float[] scales)
	{
		if (rooms == null || rooms.Count == 0)
		{
			return;
		}

		var parent = roomsParent != null ? roomsParent : transform;
		for (var i = 0; i < rooms.Count; i++)
		{
			var position = positions != null && i < positions.Length ? positions[i] : Vector3.zero;

			var roomObj = roomPrefab != null
				? Instantiate(roomPrefab, position, Quaternion.identity, parent)
				: new GameObject(rooms[i].label);

			roomObj.transform.position = position;
			roomObj.name = rooms[i].label;
			var scaleValue = scales != null && i < scales.Length ? scales[i] : 1f;
			roomObj.transform.localScale = new Vector3(scaleValue, scaleValue, 1f);

			var info = roomObj.GetComponent<RoomInfo>();
			if (info == null)
			{
				info = roomObj.AddComponent<RoomInfo>();
			}

			info.id = rooms[i].id;
			info.label = rooms[i].label;
			info.biome = rooms[i].biome;
			info.size = rooms[i].size;
			info.occupants = rooms[i].occupants.ToArray();
			info.neighbors = GetNeighborLabels(rooms, adjacency, rooms[i].id);
			if (showRoomLabels)
			{
				EnsureLabel(roomObj.transform, rooms[i].label);
			}

			if (roomPrefab == null)
			{
				roomObj.transform.SetParent(parent);
			}

			spawnedRooms.Add(roomObj);
		}
	}

	private void SpawnEdges(List<RoomData> rooms, List<HashSet<int>> adjacency, Vector3[] positions)
	{
		if (rooms == null || rooms.Count == 0 || adjacency == null || positions == null)
		{
			return;
		}

		var parent = edgesParent != null ? edgesParent : transform;
		var created = new HashSet<string>();
		for (var i = 0; i < rooms.Count; i++)
		{
			foreach (var neighborId in adjacency[i])
			{
				var key = EdgeKey(i, neighborId);
				if (created.Contains(key))
				{
					continue;
				}

				created.Add(key);
				var lineObj = new GameObject($"Edge_{rooms[i].label}_{rooms[neighborId].label}");
				lineObj.transform.SetParent(parent, false);
				var line = lineObj.AddComponent<LineRenderer>();
				line.useWorldSpace = true;
				line.startWidth = edgeWidth;
				line.endWidth = edgeWidth;
				line.startColor = edgeColor;
				line.endColor = edgeColor;
				line.material = new Material(Shader.Find("Sprites/Default"));
				line.sortingOrder = -1;

				var startPos = positions[i];
				var endPos = positions[neighborId];
				line.positionCount = 2;
				line.SetPosition(0, startPos);
				line.SetPosition(1, endPos);
				spawnedEdges.Add(lineObj);
			}
		}
	}



	private float ComputeRoomScale(RoomData room, float maxDiameter)
	{
		if (room == null)
		{
			return 1f;
		}

		var range = GetRoomSizeRange(room.biome);
		var sizeValue = RandFloat(range.x, range.y) * Mathf.Max(0.01f, roomScaleMultiplier) * GetRoomSizeMultiplier(room.size);
		if (maxDiameter > 0.01f)
		{
			var maxScale = maxDiameter * 0.9f;
			sizeValue = Mathf.Min(sizeValue, maxScale);
		}
		return Mathf.Max(0.05f, sizeValue);
	}

	private float[] ComputeRoomScales(List<RoomData> rooms, float maxDiameter)
	{
		var count = rooms?.Count ?? 0;
		var scales = new float[count];
		for (var i = 0; i < count; i++)
		{
			scales[i] = ComputeRoomScale(rooms[i], maxDiameter);
		}
		return scales;
	}

	private void ResolveOverlaps(Vector3[] positions, float[] scales, Vector2 baseSize)
	{
		if (positions == null || scales == null || positions.Length != scales.Length)
		{
			return;
		}

		var count = positions.Length;
		if (count <= 1)
		{
			return;
		}

		var baseDiameter = Mathf.Max(0.1f, Mathf.Max(baseSize.x, baseSize.y));
		for (var iter = 0; iter < Mathf.Max(1, overlapIterations); iter++)
		{
			var moved = false;
			for (var i = 0; i < count; i++)
			{
				for (var j = i + 1; j < count; j++)
				{
					var delta = positions[j] - positions[i];
					var dist = delta.magnitude;
					var radiusA = scales[i] * baseDiameter * 0.5f;
					var radiusB = scales[j] * baseDiameter * 0.5f;
					var minDist = radiusA + radiusB + overlapPadding;
					if (dist < minDist)
					{
						var dir = dist > 0.001f
							? delta / dist
							: new Vector3(UnityEngine.Random.insideUnitCircle.normalized.x, UnityEngine.Random.insideUnitCircle.normalized.y, 0f);
						var push = (minDist - dist) * 0.5f;
						positions[i] -= (Vector3)(dir * push);
						positions[j] += (Vector3)(dir * push);
						moved = true;
					}
				}
			}

			if (!moved)
			{
				break;
			}
		}
	}

	private Vector2 GetPrefabBaseSize()
	{
		if (roomPrefab == null)
		{
			return Vector2.one;
		}

		var sprite = roomPrefab.GetComponentInChildren<SpriteRenderer>();
		if (sprite != null && sprite.sprite != null)
		{
			var size = sprite.sprite.bounds.size;
			return new Vector2(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y));
		}

		var renderer = roomPrefab.GetComponentInChildren<Renderer>();
		if (renderer != null)
		{
			var worldSize = renderer.bounds.size;
			return new Vector2(Mathf.Max(0.001f, worldSize.x), Mathf.Max(0.001f, worldSize.y));
		}

		return Vector2.one;
	}

	private float ComputeMaxRoomDiameter(Vector3[] positions)
	{
		if (positions == null || positions.Length < 2)
		{
			return 0f;
		}

		var minSq = float.PositiveInfinity;
		for (var i = 0; i < positions.Length; i++)
		{
			for (var j = i + 1; j < positions.Length; j++)
			{
				var distSq = (positions[i] - positions[j]).sqrMagnitude;
				if (distSq < minSq)
				{
					minSq = distSq;
				}
			}
		}

		if (!float.IsFinite(minSq) || minSq <= 0f)
		{
			return 0f;
		}

		return Mathf.Sqrt(minSq);
	}


	private static float GetRoomSizeMultiplier(RoomSize size)
	{
		switch (size)
		{
			case RoomSize.Large:
				return 1.35f;
			case RoomSize.Small:
				return 0.85f;
			default:
				return 1f;
		}
	}

	private static RoomSize DetermineRoomSize(RoomNode node, List<HashSet<int>> adjacency)
	{
		if (node == null || adjacency == null || node.id < 0 || node.id >= adjacency.Count)
		{
			return RoomSize.Medium;
		}

		if (node.label == "START" || node.label == "GOAL")
		{
			return RoomSize.Large;
		}

		var degree = adjacency[node.id].Count;
		if (degree >= 3)
		{
			return RoomSize.Large;
		}
		if (degree == 2)
		{
			return RoomSize.Medium;
		}
		return RoomSize.Small;
	}

	private void EnsureLabel(Transform roomTransform, string label)
	{
		var labelTransform = roomTransform.Find("Label");
		TextMesh textMesh;
		if (labelTransform == null)
		{
			var labelObj = new GameObject("Label");
			labelObj.transform.SetParent(roomTransform, false);
			labelObj.transform.localPosition = new Vector3(0f, labelYOffset, 0f);
			textMesh = labelObj.AddComponent<TextMesh>();
			textMesh.anchor = TextAnchor.MiddleCenter;
			textMesh.alignment = TextAlignment.Center;
		}
		else
		{
			textMesh = labelTransform.GetComponent<TextMesh>();
			if (textMesh == null)
			{
				textMesh = labelTransform.gameObject.AddComponent<TextMesh>();
				textMesh.anchor = TextAnchor.MiddleCenter;
				textMesh.alignment = TextAlignment.Center;
			}
		}

		textMesh.text = label;
		textMesh.fontSize = labelFontSize;
		textMesh.color = labelColor;
	}

	private Vector2 GetRoomSizeRange(string biome)
	{
		if (biomeRoomSizes != null)
		{
			for (var i = 0; i < biomeRoomSizes.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(biomeRoomSizes[i].biomeName))
				{
					continue;
				}

				if (string.Equals(biomeRoomSizes[i].biomeName, biome, StringComparison.OrdinalIgnoreCase))
				{
					return biomeRoomSizes[i].sizeRange;
				}
			}
		}

		return defaultRoomSizeRange;
	}

	private string[] GetNeighborLabels(List<RoomData> rooms, List<HashSet<int>> adjacency, int id)
	{
		var labels = new List<string>();
		foreach (var neighborId in adjacency[id])
		{
			labels.Add(rooms[neighborId].label);
		}
		labels.Sort();
		return labels.ToArray();
	}

	private void ClearSpawnedRooms()
	{
		for (var i = spawnedRooms.Count - 1; i >= 0; i--)
		{
			if (spawnedRooms[i] != null)
			{
				Destroy(spawnedRooms[i]);
			}
		}

		spawnedRooms.Clear();
	}

	private void ClearSpawnedEdges()
	{
		for (var i = spawnedEdges.Count - 1; i >= 0; i--)
		{
			if (spawnedEdges[i] != null)
			{
				Destroy(spawnedEdges[i]);
			}
		}

		spawnedEdges.Clear();
	}



	private static int RandInt(int min, int max)
	{
		if (max < min)
		{
			var tmp = min;
			min = max;
			max = tmp;
		}

		return UnityEngine.Random.Range(min, max + 1);
	}

	private static float RandFloat(float min, float max)
	{
		if (max < min)
		{
			var tmp = min;
			min = max;
			max = tmp;
		}

		return UnityEngine.Random.Range(min, max);
	}

	private static T Pick<T>(T[] array)
	{
		if (array == null || array.Length == 0)
		{
			return default;
		}

		return array[RandInt(0, array.Length - 1)];
	}

	private static bool HasBiome(CreatureData creature, string biome)
	{
		if (creature == null || creature.biomes == null)
		{
			return false;
		}

		for (var i = 0; i < creature.biomes.Length; i++)
		{
			if (string.Equals(creature.biomes[i], biome, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static string EdgeKey(int a, int b)
	{
		var x = Mathf.Min(a, b);
		var y = Mathf.Max(a, b);
		return $"{x}-{y}";
	}


	[Serializable]
	private class Graph
	{
		public List<RoomNode> nodes;
		public List<HashSet<int>> adjacency;
	}

	[Serializable]
	private class RoomNode
	{
		public int id;
		public string label;
	}

	[Serializable]
	private class RoomAssignment
	{
		public string biome;
		public List<RoomData> rooms;
	}

	[Serializable]
	public class RoomData
	{
		public int id;
		public string label;
		public string biome;
		public List<string> occupants;
		public RoomSize size;
	}

	public enum RoomSize
	{
		Small,
		Medium,
		Large
	}

	[Serializable]
	private class EcosystemData
	{
		public CreatureData[] creatures;
		public BiomeData[] biomes;
	}

	[Serializable]
	private class CreatureData
	{
		public string name;
		public string[] biomes;
		public float cr;
		public string role;
		public int[] population_range;
	}

	[Serializable]
	private class BiomeData
	{
		public string name;
		public string description;
		public string light_level;
		public string water_level;
		public string temperature;
		public string[] primary_resources;
		public string[] zone_types;
	}

	[Serializable]
	private struct BiomeRoomSize
	{
		public string biomeName;
		public Vector2 sizeRange;
	}
}

public class RoomInfo : MonoBehaviour
{
	public int id;
	public string label;
	public string biome;
	public dungeonGenerator.RoomSize size;
	public string[] occupants;
	public string[] neighbors;
}
