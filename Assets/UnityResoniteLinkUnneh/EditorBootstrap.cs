using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;

public class EditorBootstrap : EditorWindow
{
    const string EditorPrefPortKey = "ResoniteLink_Port";
    int port = 45531;

    public static LinkInterface link;
    static CancellationTokenSource cts;
    string status = "Disconnected";
    static REPL_Controller replController;

    // Window UI state
    public static EditorBootstrap windowInstance;
    List<string> outputLines = new List<string>();
    Vector2 outputScroll;
    string inputCommand = "";
    TaskCompletionSource<string> inputTcs;
    public Queue<UpdateComponent> UpdateComponentqueue = new Queue<UpdateComponent>();
    public GameObject ROOTUnity;
    public GameObject TEMP;

    // Dodane pole
    private bool internalUpdateRunning = false;

    [MenuItem("Window/ResoniteLink/Connection")]
    public static void ShowWindow()
    {
        GetWindow<EditorBootstrap>("Resonite Link");
    }

    void OnEnable()
    {
        port = EditorPrefs.GetInt(EditorPrefPortKey, 45531);
        windowInstance = this;
        Cleanup();
        // Zapobiegamy podwójnej subskrypcji
        Selection.selectionChanged -= OnSelectionChanged;
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update -= EditorUpdate;
        EditorApplication.update += EditorUpdate;

    }

    void OnSelectionChanged()
    {
        var go = Selection.activeGameObject;
        if (go != null && go.tag == "Unneh")
        {
            Slot getslot = GetSlotObject(go);
            if (getslot != null)
            {
                DisplayInpectorForSlot(getslot);
            }
        }
    }
    private Dictionary<GameObject, (Vector3 position, Quaternion rotation, Vector3 scale)> previousTransforms = new Dictionary<GameObject, (Vector3, Quaternion, Vector3)>();
    float3 CreateFloat3(Vector3 vector)
    {
        float3 f3 = new float3
        {
            x = vector.x,
            y = vector.y,
            z = vector.z
        };
        return f3;
    }
    floatQ CreateFloatQ(Quaternion Quaternion)
    {
        floatQ f3 = new floatQ
        {
            x = Quaternion.x,
            y = Quaternion.y,
            z = Quaternion.z
            ,w = Quaternion.w
        };
        return f3;
    }
    async Task InternalUpdate()
    {
        if (link == null) return;

        try
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                if (go.tag != "Unneh") continue;
                var currentPos = go.transform.localPosition;
                var currentRot = go.transform.localRotation;
                var currentScale = go.transform.localScale;

                if (previousTransforms.TryGetValue(go, out var prev))
                {
                    bool hasNotChanged = currentPos == prev.position && currentRot == prev.rotation && currentScale == prev.scale;
                    if (!hasNotChanged)
                    {
                        previousTransforms[go] = (currentPos, currentRot, currentScale);
                        Slot slooot = GetSlotObject(go);
                        if (slooot == null) continue;
                        UpdateSlot updateSlot = new();
                        updateSlot.Data = slooot;

                        updateSlot.Data.Position.Value = CreateFloat3(currentPos);
                        updateSlot.Data.Rotation.Value = CreateFloatQ(currentRot);
                        updateSlot.Data.Scale.Value = CreateFloat3(currentScale);
                        updateSlot.Data.ID = ResoniteLINKID(go);

                        if (link != null)
                            await link.UpdateSlot(updateSlot);
                    }
                }
                else
                {
                    previousTransforms[go] = (currentPos, currentRot, currentScale);
                    Debug.Log($"{go.name}: Zapisano początkową transformację");
                }
            }

            var keysToRemove = new List<GameObject>();
            foreach (var kvp in previousTransforms)
            {
                if (!Selection.gameObjects.Contains(kvp.Key))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
            {
                previousTransforms.Remove(key);
            }

            while (UpdateComponentqueue.Count > 0)
            {
                var msg = UpdateComponentqueue.Dequeue();
                if (link != null)
                {
                    await link.UpdateComponent(msg);
                }
            }
        }
        catch (Exception ex)
        {
            Print("InternalUpdate caught exception: " + ex);
            Disconnect();
        }
    }

    void OnDisable()
    {
        EditorPrefs.SetInt(EditorPrefPortKey, port);

        // Odsubskrybuj eventy zanim rozłączysz
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= EditorUpdate;

        Disconnect();
        windowInstance = null;
        if (link != null)
        {
            link.Dispose();
            link = null;
        }
    }

    // Nowy dispatcher wywoływany przez EditorApplication.update
    void EditorUpdate()
    {
        // Nie uruchamiaj InternalUpdate jeśli już działa albo nie ma linka
        if (!internalUpdateRunning && link != null)
        {
            _ = InternalUpdateWrapper();
        }
    }

    async Task InternalUpdateWrapper()
    {
        internalUpdateRunning = true;
        try
        {
            await InternalUpdate();
        }
        catch (Exception ex)
        {
            Print("InternalUpdate error: " + ex);
            // Bezpiecznie rozłączamy się po błędzie sieciowym
            Disconnect();
        }
        finally
        {
            internalUpdateRunning = false;
        }
    }

    void OnGUI()
    {
        GUILayout.Label("Resonite Link", EditorStyles.boldLabel);
        port = EditorGUILayout.IntField("Port", port);

        GUILayout.Space(6);

        if (link == null)
        {
            if (GUILayout.Button("Connect"))
            {
                    ConnectAsync(port);
            }
        }
        else
        {
            if (GUILayout.Button("Disconnect"))
            {
                Disconnect();
            }
        }

        GUILayout.Space(6);
        EditorGUILayout.LabelField("Status:", status);
        if (GUILayout.Button("RedoWorld"))
        {
            if(link != null)
            {
                //RedoWorld();
                ShouldRedo = true;
            }
        }
        GUILayout.Space(8);

        // Output area
        GUILayout.Label("REPL Output:", EditorStyles.boldLabel);
        outputScroll = EditorGUILayout.BeginScrollView(outputScroll, GUILayout.Height(position.height * 0.5f));
        for (int i = 0; i < outputLines.Count; i++)
        {
            EditorGUILayout.LabelField(outputLines[i]);
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(6);

        // Input area (multiline) and controls
        GUILayout.Label("Input (Enter command):", EditorStyles.boldLabel);
        inputCommand = EditorGUILayout.TextArea(inputCommand, GUILayout.Height(Mathf.Max(60, position.height * 0.2f)));

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Send"))
        {
            SubmitInput();
        }

        if (GUILayout.Button("Clear Output"))
        {
            outputLines.Clear();
        }

        EditorGUILayout.EndHorizontal();

        // Allow Ctrl+Enter (or Cmd+Enter on mac) to submit
        var current = Event.current;
        if ((current.type == EventType.KeyDown) && (current.keyCode == KeyCode.Return) &&
            (current.control || current.command))
        {
            SubmitInput();
            current.Use();
        }
    }
    Slot ROOT;
    async void ConnectAsync(int port)
    {
        if (link != null)
        {
            Print("Already connected.");
            return;
        }
        Cleanup();

        try
        {
            EditorUtility.DisplayProgressBar("Resonite link", "Connecting...", 0);

            status = "Connecting...";
            Repaint();

            cts = new CancellationTokenSource();

            link = new LinkInterface();
            var uri = new Uri($"ws://localhost:{port}");
            await link.Connect(uri, cts.Token);

            status = "Connected";
            Print($"Connected to {uri}");
            EditorUtility.DisplayProgressBar("Resonite link", "Reciving world data...", 0.2f);

            try
            {
                await ReciveWholeWordHierarchy();
            }
            catch (Exception ex)
            {
                Print("Error while receiving world hierarchy: " + ex);
                Disconnect();
                return;
            }

            EditorUtility.DisplayProgressBar("Resonite link", "Done...", 0.99f);

            try
            {
                EditorUtility.ClearProgressBar();
                await RunLoop();
            }
            catch (OperationCanceledException)
            {
                status = "Cancelled";
                Print("Connection cancelled.");
            }
            catch (Exception ex)
            {
                Print("RunLoop error: " + ex);
                Disconnect();
            }
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
            Print("Connection cancelled.");
        }
        catch (Exception ex)
        {
            Print("ConnectAsync failed: " + ex);
            Disconnect();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }
    public async Task RunLoop()
    {
        SessionData sessionData = await link.GetSessionData();
        Print("Resonite version: " + sessionData.ResoniteVersion + "\nResoniteLink version: " + sessionData.ResoniteLinkVersion + "\nSession ID: " + sessionData.UniqueSessionId);
        try
        {
            do
            {
                if (cts.IsCancellationRequested) break;

                await InternalUpdate();
                if (ShouldRedo)
                {
                    await RedoWorld();
                    ShouldRedo = false;
                }
                await Task.Yield(); // Dodaj await, aby uniknąć blokowania wątku w nieskończonej pętli

            }
            while (!cts.IsCancellationRequested);

        }
        finally
        {
            //link?.Dispose();
            //link = null;
            //Repaint();
        }
        
      
    }

    Dictionary<string, GameObject> SlotToUnityMap = new Dictionary<string, GameObject>();
    Dictionary<GameObject, Slot> GameobjectXSlot = new();
    int count;
    Queue<Slot> DoParenting = new();

    public bool ShouldRedo { get; private set; }

    void Cleanup()
    {
        GameObject FIND = GameObject.Find("ResoniteLink");
        GameObject FINDTEMP = GameObject.Find("TempResoniteLink");
        if (FIND != null)
        {
            Undo.DestroyObjectImmediate(FIND);
        }
        if (FINDTEMP != null)
        {
            Undo.DestroyObjectImmediate(FINDTEMP);
        }
        ROOTUnity = new GameObject("ResoniteLink");
        TEMP = new GameObject("TempResoniteLink");
        if (link != null)
        {
            link.Dispose();
            link = null;
        }
        SlotToUnityMap.Clear();
        GameobjectXSlot.Clear();
    }
    async Task RedoWorld()
    {
        Cleanup();
       await ReciveWholeWordHierarchy();
    }

    async Task ReciveWholeWordHierarchy()
    {
        GetSlot getSlot = new GetSlot
        {
            SlotID = "Root",
            Depth = 5,
            IncludeComponentData = true
        };
        //Assert.IsNull(link, "Link should not be null when receiving world hierarchy.");
        
        var ff = await link.GetSlotData(getSlot);
        ROOT = ff.Data;
        CreateFromSlotToUnity(ROOT);
        DoParentingCode();
    }
    GameObject GetObject(string ID)
    {
        // Defensive: avoid ArgumentNullException from dictionary indexer and missing keys.
        if (string.IsNullOrEmpty(ID))
            return null;

        SlotToUnityMap.TryGetValue(ID, out var go);
        return go;
    }
    Slot GetSlotObject(GameObject gameObject)
    {
        if (gameObject == null) return null;
        if (GameobjectXSlot.TryGetValue(gameObject, out var slot))
            return slot;

        // Niefound — nie rzucamy wyjątku, tylko logujemy i zwracamy null.
        Print($"Warning: no Slot mapped for GameObject '{gameObject.name}' ({gameObject.GetType().Name}).");
        return null;
    }

    string ResoniteLINKID(GameObject gameObject)
    {
        if (gameObject == null) return null;
        foreach (var pair in SlotToUnityMap)
        {
            if (pair.Value == gameObject)
                return pair.Key;
        }
        return null;
    }

    void CreateFromSlotToUnity(Slot SlotData)
    {
        var name = SlotData?.Name?.Value ?? "UnnamedSlot";
        GameObject gameObject;
        if (SlotToUnityMap.TryGetValue(name, out gameObject))
        {
            gameObject.name = SlotData.Name.Value;
        }
        else
        {
            gameObject = new GameObject(name);
            gameObject.name = name;
            gameObject.tag = "Unneh";
        }
        gameObject.SetActive(SlotData.IsActive.Value);
        GameobjectXSlot[gameObject] = SlotData;
        DoParenting.Enqueue(SlotData);
        if(gameObject.transform.parent == null)
        {
            gameObject.transform.parent = TEMP.transform;
        }
        count++;
        var id = SlotData?.ID;
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString();
            Print($"Warning: slot '{name}' has null/empty ID. Generated temporary id '{id}'.");
        }
        SlotToUnityMap[id] = gameObject;

        

        if (SlotData?.Children != null)
        {
            foreach (var item in SlotData.Children)
            {
                CreateFromSlotToUnity(item);
            }
        }
    }
    void DisplayInpectorForSlot(Slot slot)
    {
        GameObject go = GetObject(slot?.ID);
        if (go == null)
        {
            Print($"Warning: No GameObject found for slot ID '{slot?.ID ?? "<null>"}' (slot name '{slot?.Name?.Value ?? "<null name>"}'). Skipping inspector display.");
            return;
        }
        SlotInspectorWindow.ShowWindow(slot);
        //Selection.activeGameObject = go;
        // Optionally, you can also ping the object in the hierarchy
        //EditorGUIUtility.PingObject(go);
    }
    void DoParentingCode()
    {
        EditorUtility.DisplayProgressBar("Resonite link", "Parenting stuff...", 0.7f);
        while (DoParenting.Count > 0)
        {
            var slot = DoParenting.Dequeue();

            var go = GetObject(slot?.ID);
            if (go == null)
            {
                Print($"Warning: No GameObject found for slot ID '{slot?.ID ?? "<null>"}' (slot name '{slot?.Name?.Value ?? "<null name>"}'). Skipping parenting.");
                continue;
            }

            GameObject parent = null;

            if (slot?.Parent == null)
            {
                parent = null;
            }
            else
            {
                var parentId = slot.Parent.TargetID;
                parent = GetObject(parentId);
            }

            if (parent != null)
            {
                go.transform.parent = parent.transform;
            }
            else
            {
                go.transform.parent = ROOTUnity.transform;
            }
            go.SetActive(slot.IsActive.Value);
            Vector3 Position = Vector3.zero;
            Quaternion Rotation = Quaternion.Euler(0, 0, 0);
            Vector3 Scale = Vector3.one;

            Position = FromResonite(slot.Position.Value);
            Rotation = FromResonite(slot.Rotation.Value);
            Scale = FromResonite(slot.Scale.Value);

            go.transform.localPosition = Position;
            go.transform.localRotation = Rotation;
            go.transform.localScale = Scale;
        }
    }
    Vector3 FromResonite(float3 float3)
    {
        return new Vector3(float3.x, float3.y, float3.z);
    }
    Quaternion FromResonite(floatQ floatq)
    {
        return new Quaternion(floatq.x, floatq.y, floatq.z, floatq.w);
    }
    void Disconnect()
    {
        EditorUtility.ClearProgressBar();

        try
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            if (link != null)
            {
                link.Dispose();
                link = null;
            }

            status = "Disconnected";
            Print("Disconnected.");
        }
        catch (Exception ex)
        {
            Print($"Disconnect error: {ex.Message}");
        }
        finally
        {
            cts = null;
            Repaint();
        }
    }

    void SubmitInput()
    {
        if (string.IsNullOrEmpty(inputCommand))
            return;

        // If a reader is waiting, deliver the command
        if (inputTcs != null && !inputTcs.Task.IsCompleted)
        {
            var cmd = inputCommand;
            inputCommand = "";
            // Complete the TCS on the main thread
            var tcs = inputTcs;
            inputTcs = null;
            EditorApplication.delayCall += () => tcs.TrySetResult(cmd);
        }
        else
        {
            // No REPL waiting; still echo into output
            AppendOutput($"> {inputCommand}");
            inputCommand = "";
        }
    }

    void AppendOutput(string text)
    {
        // Ensure UI update happens on main thread
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            outputLines.Add(text);
            // scroll to bottom
            outputScroll.y = float.MaxValue;
            Repaint();
        }
        else
        {
            EditorApplication.delayCall += () =>
            {
                outputLines.Add(text);
                outputScroll.y = float.MaxValue;
                Repaint();
            };
        }
    }

    static void Print(object msg)
    {
        Debug.Log("[ResoniteLinkUnneh] " + msg);
        // Also show in window if available
        if (windowInstance != null)
            windowInstance.AppendOutput(msg?.ToString() ?? "");
    }


}
