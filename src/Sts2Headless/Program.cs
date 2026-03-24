using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    static void Main(string[] args)
    {
        // Prevent unhandled exceptions from crashing the process
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception?.Message}");
            e.SetObserved();
        };

        // Set up assembly resolution to find game DLLs
        var libDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib");
        if (!Directory.Exists(libDir))
            libDir = Path.Combine(AppContext.BaseDirectory, "lib");

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            // Also check game directory (via STS2_GAME_DIR env var)
            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        var sim = new RunSimulator();
        WriteLine(new Dictionary<string, object?> { ["type"] = "ready", ["version"] = "0.2.0" });

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            Dictionary<string, object?>? result;
            try
            {
                var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                result = HandleCommand(sim, cmd);
            }
            catch (JsonException ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}" };
            }

            if (result != null)
                WriteLine(result);
        }
    }

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        switch (cmdType)
        {
            case "start_run":
                return sim.StartRun(
                    cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var s) ? s.GetString() : null,
                    cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en"
                );

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "load_save":
            {
                var savePath = cmd.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                var saveJson = cmd.TryGetProperty("json", out var sj) ? sj.GetString() : null;
                if (saveJson == null && savePath != null)
                {
                    if (!File.Exists(savePath))
                        return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Save file not found: {savePath}" };
                    saveJson = File.ReadAllText(savePath);
                }
                if (saveJson == null)
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "Provide 'path' or 'json' for load_save" };
                return sim.LoadSave(saveJson);
            }

            case "get_map":
                return sim.GetFullMap();

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                return sim.SetPlayer(args);
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                return sim.EnterRoom(roomType, encounter, eventId);
            }

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                return sim.SetDrawOrder(cards);
            }

            case "save_game":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                return sim.SaveGame(outputPath);
            }

            case "inspect_api":
            {
                var targets = new[] { "SaveManager", "RunState" };
                var typeFilter = cmd.TryGetProperty("type", out var tf) ? tf.GetString() : null;
                if (typeFilter != null) targets = new[] { typeFilter };
                
                var results = new List<Dictionary<string, object?>>();
                // Load sts2.dll directly
                var sts2LibDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib");
                if (!Directory.Exists(sts2LibDir))
                    sts2LibDir = Path.Combine(AppContext.BaseDirectory, "lib");
                var sts2Path = Path.Combine(sts2LibDir, "sts2.dll");
                var assemblies = new List<System.Reflection.Assembly>(AppDomain.CurrentDomain.GetAssemblies());
                if (File.Exists(sts2Path))
                {
                    try { assemblies.Add(System.Reflection.Assembly.LoadFrom(Path.GetFullPath(sts2Path))); }
                    catch { }
                }
                // Also try typeof(MegaCrit.Sts2.Core.Saves.SaveManager).Assembly
                try { assemblies.Add(typeof(MegaCrit.Sts2.Core.Saves.SaveManager).Assembly); }
                catch { }
                
                var seen = new HashSet<string>();
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }
                    
                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        if (!targets.Any(x => t.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                        if (!seen.Add(t.FullName ?? "")) continue;
                        var methods = new List<string>();
                        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic 
                            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static 
                            | System.Reflection.BindingFlags.DeclaredOnly;
                        foreach (var m in t.GetMethods(flags).OrderBy(m => m.Name))
                        {
                            var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                            var mods = m.IsStatic ? "static " : "";
                            methods.Add($"{mods}{m.ReturnType.Name} {m.Name}({parms})");
                        }
                        var props = t.GetProperties(flags).Select(p => $"{p.PropertyType.Name} {p.Name}").ToList();
                        results.Add(new Dictionary<string, object?>
                        {
                            ["type_name"] = t.FullName,
                            ["base"] = t.BaseType?.FullName,
                            ["methods"] = methods,
                            ["properties"] = props,
                        });
                    }
                }
                return new Dictionary<string, object?> { ["type"] = "api_info", ["types"] = results };
            }

            case "quit":
                sim.CleanUp();
                return null;

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    static void WriteLine(Dictionary<string, object?> data)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
        Console.Out.Flush();
    }
}
