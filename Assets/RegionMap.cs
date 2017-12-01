using System.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;
using Mapzen.VectorData;
using Mapzen.Unity;
using Mapzen.VectorData.Filters;
using Mapzen.VectorData.Formats;
using Mapzen;

namespace Mapzen
{
    public class RegionMap : MonoBehaviour
    {
        // Public fields
        // These are serialized, so renaming them will break asset compatibility.

        public string ApiKey = "";

        public TileArea Area = new TileArea(
            new LngLat(-74.014892578125, 40.70562793820589),
            new LngLat(-74.00390625, 40.713955826286046),
            16);

        public float UnitsPerMeter = 1.0f;

        public string RegionName = "";

        public SceneGroupType GroupOptions;

        public GameObjectOptions GameObjectOptions;

        public List<MapStyle> Styles = new List<MapStyle>();

        // Private fields

        private List<GameObject> tiles = new List<GameObject>();

        private IO tileIO = new IO();

        private List<TileTask> tasks = new List<TileTask>();

        private int nTasksForArea = 0;

        private int generation = 0;

        private AsyncWorker worker = new AsyncWorker(2);

        private GameObject regionMap;

        private Dictionary<TileAddress, IEnumerable<FeatureCollection>> tileCache = new Dictionary<TileAddress, IEnumerable<FeatureCollection>>();

        public List<GameObject> Tiles
        {
            get { return tiles; }
        }

        public void DownloadTilesAsync()
        {
            TileBounds bounds = new TileBounds(Area);

            // Abort currently running tasks and increase generation
            worker.ClearTasks();
            tasks.Clear();
            nTasksForArea = 0;
            generation++;

            foreach (var tileAddress in bounds.TileAddressRange)
            {
                nTasksForArea++;
            }

            // Use a local generation variable to be used in IORequestCallback coroutine
            int requestGeneration = generation;

            foreach (var tileAddress in bounds.TileAddressRange)
            {
                float offsetX = (tileAddress.x - bounds.min.x);
                float offsetY = (-tileAddress.y + bounds.min.y);

                float scaleRatio = (float)tileAddress.GetSizeMercatorMeters() * UnitsPerMeter;
                Matrix4x4 scale = Matrix4x4.Scale(new Vector3(scaleRatio, scaleRatio, scaleRatio));
                Matrix4x4 translate = Matrix4x4.Translate(new Vector3(offsetX * scaleRatio, 0.0f, offsetY * scaleRatio));
                Matrix4x4 transform = translate * scale;

                IEnumerable<FeatureCollection> featureCollections = null;

                lock (tileCache)
                {
                    tileCache.TryGetValue(tileAddress, out featureCollections);
                }

                if (featureCollections != null)
                {
                    var task = new TileTask(Styles, tileAddress, transform, generation);

                    worker.RunAsync(() =>
                    {
                        if (currentGeneration == task.Generation)
                        {
                            task.Start(featureCollections);
                            tasks.Add(task);
                        }
                    });
                }
                else
                {
                    var wrappedTileAddress = tileAddress.Wrapped();

                    var uri = new Uri(string.Format("https://tile.mapzen.com/mapzen/vector/v1/all/{0}/{1}/{2}.mvt?api_key={3}",
                        wrappedTileAddress.z,
                        wrappedTileAddress.x,
                        wrappedTileAddress.y,
                        ApiKey));

                    IO.IORequestCallback onTileFetched = (response) =>
                    {
                        if (requestGeneration != generation)
                        {
                            // Another request has been made before the coroutine was triggered
                            return;
                        }

                        if (response.hasError())
                        {
                            Debug.Log("TileIO Error: " + response.error);
                            return;
                        }

                        if (response.data.Length == 0)
                        {
                            Debug.Log("Empty Response");
                            return;
                        }

                        float offsetX = (tileAddress.x - bounds.min.x);
                        float offsetY = (-tileAddress.y + bounds.min.y);

                        float scaleRatio = (float)tileAddress.GetSizeMercatorMeters() * UnitsPerMeter;
                        Matrix4x4 scale = Matrix4x4.Scale(new Vector3(scaleRatio, scaleRatio, scaleRatio));
                        Matrix4x4 translate = Matrix4x4.Translate(new Vector3(offsetX * scaleRatio, 0.0f, offsetY * scaleRatio));
                        Matrix4x4 transform = translate * scale;

                        var task = new TileTask(Styles, tileAddress, transform, generation);

                        worker.RunAsync(() =>
                        {
                            // Skip any tasks that have been generated for a different generation
                            if (generation == task.Generation)
                            {
                                // var tileData = new GeoJsonTile(address, response);
                                var mvtTile = new MvtTile(tileAddress, response.data);

                                lock (tileCache)
                                {
                                    tileCache.Add(tileAddress, mvtTile.FeatureCollections);
                                }

                                task.Start(mvtTile.FeatureCollections);
                                tasks.Add(task);
                            }
                        });
                    };

                    // Starts the HTTP request
                    StartCoroutine(tileIO.FetchNetworkData(uri, onTileFetched));
                }
            }
        }

        public bool HasPendingTasks()
        {
            return nTasksForArea > 0;
        }

        public bool FinishedRunningTasks()
        {
            // Number of tasks ready for the current generation
            int nTasksReady = 0;

            foreach (var task in tasks)
            {
                if (task.Generation == generation)
                {
                    nTasksReady++;
                }
            }

            return nTasksReady == nTasksForArea;
        }

        public void GenerateSceneGraph()
        {
            if (regionMap != null)
            {
                DestroyImmediate(regionMap);
            }

            // Merge all feature meshes
            List<FeatureMesh> features = new List<FeatureMesh>();
            foreach (var task in tasks)
            {
                if (task.Generation == generation)
                {
                    features.AddRange(task.Data);
                }
            }

            tasks.Clear();
            nTasksForArea = 0;

            var mapRegion = new GameObject(RegionName);
            var sceneGraph = new SceneGraph(mapRegion, GroupOptions, GameObjectOptions, features);

            sceneGraph.Generate();
        }
    }
}
