using ResoniteLink;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using UnityEditor;
using UnityEngine;

public class SlotInspectorWindow : EditorWindow
{
    Slot currentSlot;
    Vector2 scroll;
    // przechowuj edytowalne JSONy dla komponentów (klucz: indeks komponentu)
    Dictionary<int, string> componentJsonCache = new Dictionary<int, string>();
    // stany foldout dla komponentów (klucz: indeks komponentu)
    Dictionary<int, bool> componentFoldouts = new Dictionary<int, bool>();

    static JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        AllowTrailingCommas = true
    };

    public static void ShowWindow(Slot slot)
    {
        var w = GetWindow<SlotInspectorWindow>("Slot Inspector");
        w.currentSlot = slot;
        w.componentJsonCache.Clear();
        w.componentFoldouts.Clear();
        w.minSize = new Vector2(300, 200);
        w.Show();
    }

    void OnGUI()
    {
        if (currentSlot == null)
        {
            GUILayout.Label("No slot for display.", EditorStyles.boldLabel);
            return;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Slot:", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Name", currentSlot?.Name?.Value ?? "<null>");
        EditorGUILayout.LabelField("ID", currentSlot?.ID ?? "<null>");
        if (currentSlot.IsActive != null)
            EditorGUILayout.LabelField("IsActive", currentSlot.IsActive.Value.ToString());

        EditorGUILayout.Space();

        // Transformy
        EditorGUILayout.LabelField("Transform (Resonite):", EditorStyles.boldLabel);
        if (currentSlot.Position != null)
            EditorGUILayout.LabelField("Position", JsonSerializer.Serialize(currentSlot.Position.Value, jsonOptions));
        if (currentSlot.Rotation != null)
            EditorGUILayout.LabelField("Rotation", JsonSerializer.Serialize(currentSlot.Rotation.Value, jsonOptions));
        if (currentSlot.Scale != null)
            EditorGUILayout.LabelField("Scale", JsonSerializer.Serialize(currentSlot.Scale.Value, jsonOptions));

        EditorGUILayout.Space();

        // Komponenty
        EditorGUILayout.LabelField("Components:", EditorStyles.boldLabel);
        if (currentSlot.Components != null)
        {
            int index = 0;
            foreach (var item in currentSlot.Components)
            {
                // Dodaj domyœlny stan foldout, jeœli nie istnieje
                if (!componentFoldouts.ContainsKey(index))
                    componentFoldouts[index] = true;

                // Foldout dla komponentu
                componentFoldouts[index] = EditorGUILayout.Foldout(componentFoldouts[index], item.ComponentType ?? $"Component {index}", EditorStyles.foldout);
                if (componentFoldouts[index])
                {
                    foreach (var Syncs in item.Members)
                    {
                        // Renderujemy ka¿dy wiersz w poziomie: kontrolka (bez przycisku Select, bez highlightu)
                        GUILayout.BeginHorizontal();

                        // Syncs.Key = nazwa pola, Syncs.Value = obiekt pola np. Field_bool, Field_string, Field_float3 itd.
                        var memberName = Syncs.Key;
                        var fieldObj = Syncs.Value;
                        if (fieldObj == null)
                        {
                            EditorGUILayout.LabelField(memberName, "<null>");
                            GUILayout.EndHorizontal();
                            continue;
                        }

                        var fType = fieldObj.GetType();
                        var tName = fType.Name; // np. "Field_bool", "Field_string", "Field_float3"

                        // Pobierz PropertyInfo dla 'Value' jeœli istnieje
                        var valueProp = fType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        object currentVal = null;
                        if (valueProp != null)
                        {
                            currentVal = valueProp.GetValue(fieldObj);
                        }

                        // Obs³uga najczêstszych typów z wykrywaniem zmiany wartoœci
                        if (tName == "Field_bool" && valueProp != null)
                        {
                            bool oldVal = false;
                            try { oldVal = currentVal is bool b ? b : Convert.ToBoolean(currentVal); } catch { oldVal = false; }
                            bool newVal = EditorGUILayout.Toggle(memberName, oldVal);
                            if (newVal != oldVal)
                            {
                                valueProp.SetValue(fieldObj, newVal);
                                OnMemberValueChanged(index, memberName, oldVal, newVal);
                            }
                        }
                        else if (tName == "Field_string" && valueProp != null)
                        {
                            string oldVal = currentVal?.ToString() ?? "";
                            string newVal = EditorGUILayout.TextField(memberName, oldVal);
                            if (newVal != oldVal)
                            {
                                valueProp.SetValue(fieldObj, newVal);
                                OnMemberValueChanged(index, memberName, oldVal, newVal);
                            }
                        }
                        else if ((tName == "Field_float" || tName == "Field_double") && valueProp != null)
                        {
                            float oldF = 0f;
                            try { oldF = Convert.ToSingle(currentVal); } catch { oldF = 0f; }
                            float newF = EditorGUILayout.FloatField(memberName, oldF);
                            if (Math.Abs(newF - oldF) > float.Epsilon)
                            {
                                if (valueProp.PropertyType == typeof(double))
                                    valueProp.SetValue(fieldObj, (double)newF);
                                else
                                    valueProp.SetValue(fieldObj, newF);
                                OnMemberValueChanged(index, memberName, oldF, newF);
                            }
                        }
                        else if ((tName == "Field_long" || tName == "Field_int") && valueProp != null)
                        {
                            long oldL = 0;
                            try { oldL = Convert.ToInt64(currentVal); } catch { oldL = 0; }
                            long newL = EditorGUILayout.LongField(memberName, oldL);
                            if (newL != oldL)
                            {
                                if (valueProp.PropertyType == typeof(int))
                                    valueProp.SetValue(fieldObj, (int)newL);
                                else
                                    valueProp.SetValue(fieldObj, newL);
                                OnMemberValueChanged(index, memberName, oldL, newL);
                            }
                        }
                        else if (tName == "Field_float3" && valueProp != null)
                        {
                            var val = currentVal;
                            if (val != null)
                            {
                                var vx = GetFloatMember(val, "x");
                                var vy = GetFloatMember(val, "y");
                                var vz = GetFloatMember(val, "z");
                                var oldVec = new Vector3(vx, vy, vz);
                                var newVec = EditorGUILayout.Vector3Field(memberName, oldVec);
                                if (newVec != oldVec)
                                {
                                    SetFloatMember(val, "x", newVec.x);
                                    SetFloatMember(val, "y", newVec.y);
                                    SetFloatMember(val, "z", newVec.z);
                                    valueProp.SetValue(fieldObj, val);
                                    OnMemberValueChanged(index, memberName, oldVec, newVec);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField(memberName, "<null float3>");
                            }
                        }
                        else if (tName == "Field_floatQ" && valueProp != null)
                        {
                            var val = currentVal;
                            if (val != null)
                            {
                                var qx = GetFloatMember(val, "x");
                                var qy = GetFloatMember(val, "y");
                                var qz = GetFloatMember(val, "z");
                                var qw = GetFloatMember(val, "w");
                                var oldQuat = new Quaternion(qx, qy, qz, qw);
                                var oldEuler = oldQuat.eulerAngles;
                                var newEuler = EditorGUILayout.Vector3Field(memberName + " (Euler)", oldEuler);
                                if (newEuler != oldEuler)
                                {
                                    var newQuat = Quaternion.Euler(newEuler);
                                    SetFloatMember(val, "x", newQuat.x);
                                    SetFloatMember(val, "y", newQuat.y);
                                    SetFloatMember(val, "z", newQuat.z);
                                    SetFloatMember(val, "w", newQuat.w);
                                    valueProp.SetValue(fieldObj, val);
                                    OnMemberValueChanged(index, memberName, oldEuler, newEuler);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField(memberName, "<null floatQ>");
                            }
                        }
                        else
                        {
                            // Fallback: poka¿ jako JSON (nieedytowalne)
                            string json = "<unserializable>";
                            try { json = JsonSerializer.Serialize(currentVal, jsonOptions); } catch { json = currentVal?.ToString() ?? "<null>"; }
                            EditorGUILayout.LabelField(memberName, json);
                        }

                        GUILayout.EndHorizontal();
                    }
                }
                index++;
            }
        }

        if (GUILayout.Button("Close"))
        {
            Close();
        }

        EditorGUILayout.EndScrollView();
    }

    // Wywo³ywane gdy wartoœæ cz³onka komponentu siê zmieni³a
    void OnMemberValueChanged(int componentIndex, string memberKey, object oldValue, object newValue)
    {
        // Krótki log — mo¿na tu wys³aæ update do serwera / oznaczyæ slot jako zmieniony / zapisaæ undo itp.
        Print($"Member changed — component {componentIndex} member '{memberKey}': {ToShortString(oldValue)} -> {ToShortString(newValue)}");
        UpdateComponent updateComponent = new UpdateComponent();
        updateComponent.Data = currentSlot.Components[componentIndex];
        updateComponent.Data.Members[memberKey] = currentSlot.Components[componentIndex].Members[memberKey];
        //EditorBootstrap.link?.UpdateComponent(updateComponent);
        EditorBootstrap.windowInstance.UpdateComponentqueue.Enqueue(updateComponent);
        // Przyk³ad: odœwie¿ cache json jeœli potrzebujesz
        componentJsonCache.Remove(componentIndex);

        // TODO: jeœli chcesz wysy³aæ update do Resonite (link.UpdateComponent itd.) — dodam to na ¿yczenie.
    }

    static string ToShortString(object o)
    {
        if (o == null) return "<null>";
        if (o is Vector3 v) return $"Vector3({v.x:F3},{v.y:F3},{v.z:F3})";
        if (o is Quaternion q) return $"Quat({q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3})";
        return o.ToString();
    }

    // Pomocnicze funkcje refleksyjne do odczytu/zapisu pól x,y,z,w w strukturach float3/floatQ
    static float GetFloatMember(object obj, string memberName)
    {
        if (obj == null) return 0f;
        var t = obj.GetType();
        var f = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) as PropertyInfo;
        if (f != null)
        {
            try { return Convert.ToSingle(f.GetValue(obj)); } catch { }
        }
        var field = t.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            try { return Convert.ToSingle(field.GetValue(obj)); } catch { }
        }
        return 0f;
    }

    static void SetFloatMember(object obj, string memberName, float value)
    {
        if (obj == null) return;
        var t = obj.GetType();
        var f = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) as PropertyInfo;
        if (f != null && f.CanWrite)
        {
            try { f.SetValue(obj, Convert.ChangeType(value, f.PropertyType)); return; } catch { }
        }
        var field = t.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            try { field.SetValue(obj, Convert.ChangeType(value, field.FieldType)); } catch { }
        }
    }

    static void Print(object msg)
    {
        Debug.Log("[SlotInspector] " + msg);
        // dodatkowo: append to EditorBootstrap output jeœli dostêpne
        if (EditorBootstrap.windowInstance != null)
            EditorBootstrap.windowInstance?.GetType()
                .GetMethod("AppendOutput", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(EditorBootstrap.windowInstance, new object[] { msg?.ToString() ?? "" });
    }
}