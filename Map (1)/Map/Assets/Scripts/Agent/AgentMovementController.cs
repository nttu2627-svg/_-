using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


[DisallowMultipleComponent]
public class AgentMovementController : MonoBehaviour
{
    private struct PathNode
    {
        public Vector2 position;
        public PortalController portal;

        public PathNode(Vector2 position, PortalController portal)
        {
            this.position = position;
            this.portal = portal;
        }

        public bool IsPortal => portal != null;
    }

    private const string ExteriorKey = "EXTERIOR";

    [SerializeField, Tooltip("移動速度倍率（會乘上代理人自身的速度設定）")]
    private float _speedMultiplier = 1f;
    [SerializeField, Tooltip("節點抵達閾值（公尺）")]
    private float _nodeArrivalThreshold = 0.08f;

    private AgentController _agent;
    private Teleportable2D _teleportable;
    private Transform _transform;
    private float _baseSpeed = 4.5f;
    private float _baseArrivalThreshold = 0.05f;
    private Coroutine _movementRoutine;
    private readonly List<PathNode> _currentPath = new List<PathNode>();
    private Dictionary<string, Transform> _locationLookup;
    private Dictionary<string, List<PortalController>> _portalsByBuilding;
    private readonly List<PortalController> _allPortals = new List<PortalController>();
    private bool _isMoving;
    private bool _hasLastRequestedTarget = false;
    private Vector3 _lastRequestedTarget = Vector3.positiveInfinity;
    private string _lastRequestedLocation = null;
    private static readonly List<PortalController> GlobalPortalCache = new List<PortalController>();
    private static readonly Dictionary<string, List<PortalController>> GlobalPortalsByBuilding = new Dictionary<string, List<PortalController>>(StringComparer.OrdinalIgnoreCase);
    private static float _globalPortalCacheTimestamp = -99f;
    private const float PortalCacheDuration = 5f;    public bool IsControllingMovement => _isMoving || _movementRoutine != null;

    public void ConfigureFromAgent(AgentController agent, float moveSpeed, float arrivalThreshold)
    {
        _agent = agent;
        _transform = agent != null ? agent.transform : transform;
        _teleportable = GetComponent<Teleportable2D>();
        if (_teleportable == null)
        {
            _teleportable = gameObject.AddComponent<Teleportable2D>();
        }

        _baseSpeed = Mathf.Max(0.1f, moveSpeed);
        _baseArrivalThreshold = Mathf.Max(0.01f, arrivalThreshold);
        _nodeArrivalThreshold = Mathf.Max(0.01f, arrivalThreshold * 0.5f);
    }

    public void RegisterLocations(Dictionary<string, Transform> locations)
    {
        _locationLookup = locations;
    }

    public void RequestPathTo(string locationName, Vector3 worldPosition, Transform locationTransform)
    {
        EnsurePortalLookup();

        Transform destination = locationTransform;
        if (destination == null && !string.IsNullOrWhiteSpace(locationName))
        {
            if (_locationLookup != null && _locationLookup.TryGetValue(locationName, out Transform cached) && cached != null)
            {
                destination = cached;
            }
            else if (_agent != null && _agent.TryFindLocationTransform(locationName, out Transform resolved) && resolved != null)
            {
                destination = resolved;
            }
        }
        if (_hasLastRequestedTarget)
        {
            bool sameLocation = string.Equals(_lastRequestedLocation, locationName, StringComparison.OrdinalIgnoreCase) ||
                                (string.IsNullOrEmpty(_lastRequestedLocation) && string.IsNullOrEmpty(locationName));
            if (sameLocation && (worldPosition - _lastRequestedTarget).sqrMagnitude < 0.0001f)
            {
                return;
            }
        }

        _lastRequestedTarget = worldPosition;
        _lastRequestedLocation = locationName;
        _hasLastRequestedTarget = true;
        List<PathNode> path = BuildPath(worldPosition, destination, locationName);
        if (path.Count == 0)
        {
            CancelMovement();
            return;
        }

        Vector2[] nodes = ExtractPositions(path);
        StartPath(path, nodes);
    }

    public void HandleTeleport(Vector3 newPosition)
    {
        CancelMovementInternal();
        _hasLastRequestedTarget = false;
        if (_transform != null)
        {
            _transform.position = new Vector3(newPosition.x, newPosition.y, _transform.position.z);
        }
        _agent?.ForceImmediateVisualRefresh();
    }

    public void CancelMovement()
    {
        CancelMovementInternal();
        _hasLastRequestedTarget = false;
        _agent?.NotifyMovementCompleted();
    }

    public IEnumerator MoveAlongPath(Vector2[] nodes)
    {
        if (nodes == null || nodes.Length == 0)
        {
            _isMoving = false;
            _movementRoutine = null;
            _hasLastRequestedTarget = false; 
            yield break;
        }

        _isMoving = true;
        _agent?.NotifyMovementStarted();

        for (int i = 0; i < nodes.Length; i++)
        {
            if (_transform == null)
            {
                break;
            }

            PathNode step = _currentPath[i];
            Vector3 target = new Vector3(nodes[i].x, nodes[i].y, _transform.position.z);
            float threshold = _nodeArrivalThreshold;
            if (step.IsPortal && _teleportable != null)
            {
                threshold = Mathf.Max(threshold, _teleportable.PortalSearchPadding);
            }
            threshold = Mathf.Max(threshold, _baseArrivalThreshold);

            while (Vector2.Distance(_transform.position, target) > threshold)
            {
                float speed = _baseSpeed * Mathf.Max(0.1f, _speedMultiplier);
                _transform.position = Vector3.MoveTowards(_transform.position, target, speed * Time.deltaTime);
                yield return null;
            }

            _transform.position = target;

            if (step.IsPortal)
            {
                yield return ExecutePortal(step.portal);
            }
        }

        _isMoving = false;
        _movementRoutine = null;
        _hasLastRequestedTarget = false;
        _agent?.NotifyMovementCompleted();
    }

    private void StartPath(List<PathNode> path, Vector2[] nodes)
    {
        CancelMovementInternal();
        _currentPath.Clear();
        _currentPath.AddRange(path);
        _movementRoutine = StartCoroutine(MoveAlongPath(nodes));
    }

    private void CancelMovementInternal()
    {
        if (_movementRoutine != null)
        {
            StopCoroutine(_movementRoutine);
            _movementRoutine = null;
        }
        _isMoving = false;
        _currentPath.Clear();
    }

    private Vector2[] ExtractPositions(List<PathNode> path)
    {
        Vector2[] nodes = new Vector2[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            nodes[i] = path[i].position;
        }
        return nodes;
    }

    private IEnumerator ExecutePortal(PortalController portal)
    {
        if (portal == null)
        {
            yield break;
        }

        bool success = portal.TryTeleport(_transform, _teleportable);
        if (success)
        {
            yield return null;
        }
    }

    private List<PathNode> BuildPath(Vector3 destination, Transform destinationTransform, string locationName)
    {
        var result = new List<PathNode>();

        string startBuilding = DetermineCurrentBuilding();
        string destinationBuilding = DetermineDestinationBuilding(destinationTransform, locationName);

        // 嘗試尋找可用的傳送門，即使目標與起點在同一棟建築內部也一樣。
        PortalController entryPortal = FindPortalPair(startBuilding, destinationBuilding, _transform.position);
        if (entryPortal != null)
        {
            result.Add(new PathNode(entryPortal.transform.position, entryPortal));

            PortalController exitPortal = entryPortal.TargetPortal;
            if (exitPortal != null)
            {
                Vector3 exitPosition = exitPortal.GetExitPosition(_transform);
                result.Add(new PathNode(exitPosition, null));
            }
        }

        // 最終將目的地本身加入路徑
        result.Add(new PathNode(new Vector2(destination.x, destination.y), null));

        return result;
    }

    private string DetermineCurrentBuilding()
    {
        if (_agent == null)
        {
            return ExteriorKey;
        }

        string building = _agent.GuessCurrentBuilding();
        return string.IsNullOrEmpty(building) ? ExteriorKey : building;
    }

    private string DetermineDestinationBuilding(Transform destinationTransform, string locationName)
    {
        if (_agent == null)
        {
            return ExteriorKey;
        }

        string building = _agent.GetBuildingFromTransform(destinationTransform);
        if (!string.IsNullOrEmpty(building))
        {
            return building;
        }

        building = _agent.GuessBuildingForLocation(locationName);
        return string.IsNullOrEmpty(building) ? ExteriorKey : building;
    }

    private PortalController FindPortalPair(string startBuilding, string destinationBuilding, Vector3 originPosition)
    {
        EnsurePortalLookup();

        PortalController bestEntry = null;
        float bestScore = float.MaxValue;

        foreach (PortalController portal in _allPortals)
        {
            if (portal == null || portal.TargetPortal == null)
            {
                continue;
            }

            string entryBuilding = DeterminePortalBuilding(portal);
            string exitBuilding = DeterminePortalBuilding(portal.TargetPortal);

            if (!string.IsNullOrEmpty(startBuilding) &&
                !string.Equals(entryBuilding, startBuilding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(destinationBuilding) &&
                !string.Equals(exitBuilding, destinationBuilding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float score = ((Vector2)portal.transform.position - (Vector2)originPosition).sqrMagnitude;
            if (score < bestScore)
            {
                bestScore = score;
                bestEntry = portal;
            }
        }

        if (bestEntry == null)
        {
            bestEntry = FindNearestPortal(startBuilding, originPosition);
        }

        return bestEntry;
    }

    private PortalController FindNearestPortal(string buildingKey, Vector3 originPosition)
    {
        EnsurePortalLookup();

        PortalController closest = null;
        float bestSqr = float.MaxValue;

        if (!string.IsNullOrEmpty(buildingKey) &&
            _portalsByBuilding != null &&
            _portalsByBuilding.TryGetValue(buildingKey, out List<PortalController> candidates))
        {
            EvaluatePortalList(candidates, originPosition, ref closest, ref bestSqr);
        }

        if (closest == null)
        {
            foreach (var pair in _portalsByBuilding)
            {
                EvaluatePortalList(pair.Value, originPosition, ref closest, ref bestSqr);
            }
        }

        return closest;
    }

    private void EvaluatePortalList(List<PortalController> portals, Vector3 origin, ref PortalController closest, ref float bestSqr)
    {
        if (portals == null)
        {
            return;
        }

        foreach (PortalController portal in portals)
        {
            if (portal == null)
            {
                continue;
            }

            float sqr = ((Vector2)portal.transform.position - (Vector2)origin).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                closest = portal;
            }
        }
    }

    private void EnsurePortalLookup()
    {
        if (Time.time - _globalPortalCacheTimestamp > PortalCacheDuration || GlobalPortalCache.Count == 0)
        {
            RefreshPortalCache();
        }

        if (_allPortals.Count != GlobalPortalCache.Count)
        {
            _allPortals.Clear();
            _allPortals.AddRange(GlobalPortalCache);
        }

        if (_portalsByBuilding == null)
        {
            _portalsByBuilding = new Dictionary<string, List<PortalController>>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _portalsByBuilding.Clear();
        }

        foreach (var pair in GlobalPortalsByBuilding)
        {
            if (pair.Value == null || pair.Value.Count == 0)
            {
                continue;
            }

            _portalsByBuilding[pair.Key] = pair.Value;
        }
    }

    private static void RefreshPortalCache()
    {
        PortalController[] portals;

#if UNITY_2023_1_OR_NEWER
        portals = FindObjectsByType<PortalController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
#else
        portals = FindObjectsOfType<PortalController>(true);
#endif

        GlobalPortalCache.Clear();
        GlobalPortalsByBuilding.Clear();

        foreach (var portal in portals)
        {
            if (!portal) continue;

            GlobalPortalCache.Add(portal);

            string building = DeterminePortalBuilding(portal);
            if (!GlobalPortalsByBuilding.TryGetValue(building, out var list))
            {
                list = new List<PortalController>();
                GlobalPortalsByBuilding[building] = list;
            }

            list.Add(portal);
        }

        _globalPortalCacheTimestamp = Time.time;
    }

    private static string DeterminePortalBuilding(PortalController portal)
    {
        if (portal == null)
        {
            return ExteriorKey;
        }

        BuildingController building = portal.GetComponentInParent<BuildingController>();
        if (building != null && !string.IsNullOrEmpty(building.buildingName))
        {
            return building.buildingName;
        }

        return ExteriorKey;
    }
}