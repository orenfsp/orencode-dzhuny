using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Orencode;

// One JSON document per entity; all command mutations are committed atomically.
// Single application instance, deliberately small hackathon dataset.
public sealed class Store
{
    public static readonly string[] Tables = ["accounts", "people", "organizations", "events", "applications", "results", "qualifications", "roster", "links", "assignments", "consents", "logs", "notifications", "outbox", "documents", "groups", "lessons", "standards", "assessments", "prizeRules", "petitions", "certificates", "tokens", "clients", "codes", "oidcTokens", "ratingRules"];
    private readonly object gate = new();
    private readonly string connection;
    public string DataDirectory { get; }
    public Store(IConfiguration config)
    {
        var path = Path.GetFullPath(config["Database:Path"] ?? "data/orencode.db");
        DataDirectory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(DataDirectory);
        connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS schema_versions(version INTEGER PRIMARY KEY); INSERT OR IGNORE INTO schema_versions VALUES(1);");
        foreach (var table in Tables) Execute(db, $"CREATE TABLE IF NOT EXISTS [{table}](id TEXT PRIMARY KEY, payload TEXT NOT NULL CHECK(json_valid(payload)));");
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }
    private static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static JsonObject Load(SqliteConnection db)
    {
        var state = new JsonObject();
        foreach (var table in Tables)
        {
            var rows = new JsonArray(); using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT payload FROM [{table}] ORDER BY rowid";
            using var reader = cmd.ExecuteReader(); while (reader.Read()) rows.Add(JsonNode.Parse(reader.GetString(0)));
            state[table] = rows;
        }
        return state;
    }
    public T Read<T>(Func<JsonObject,T> query) { lock(gate) { using var db = Open(); using var tx=db.BeginTransaction(); return query(Load(db)); } }
    public T Write<T>(Func<JsonObject,T> action)
    {
        lock(gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred:false);
            var state=Load(db); var result=action(state);
            foreach(var table in Tables)
            {
                Execute(db,$"DELETE FROM [{table}]");
                foreach(var row in state.Rows(table))
                {
                    using var cmd=db.CreateCommand(); cmd.CommandText=$"INSERT INTO [{table}](id,payload) VALUES($id,$data)";
                    cmd.Parameters.AddWithValue("$id",row.S("id")); cmd.Parameters.AddWithValue("$data",row.ToJsonString()); cmd.ExecuteNonQuery();
                }
            }
            tx.Commit(); return result;
        }
    }
}
public sealed class DomainError(string message,int status=400):Exception(message) { public int Status {get;}=status; }
public static class J
{
    public static string S(this JsonNode? n,string key,string fallback="") => n?[key]?.GetValue<string>() ?? fallback;
    public static int I(this JsonNode? n,string key,int fallback=0) => n?[key]?.GetValue<int>() ?? fallback;
    public static double N(this JsonNode? n,string key,double fallback=0) => n?[key]?.GetValue<double>() ?? fallback;
    public static bool B(this JsonNode? n,string key) => n?[key]?.GetValue<bool>() ?? false;
    public static string[] Strings(this JsonNode? n,string key) => n?[key]?.AsArray().Select(x=>x!.GetValue<string>()).ToArray() ?? [];
    public static IEnumerable<JsonObject> Rows(this JsonObject d,string table) => d[table]!.AsArray().Select(x=>x!.AsObject());
    public static JsonObject Get(this JsonObject d,string table,string id) => d.Rows(table).FirstOrDefault(x=>x.S("id")==id) ?? throw new DomainError("Запись не найдена",404);
    public static JsonObject Add(this JsonObject d,string table,object value)
    {
        var o=System.Text.Json.JsonSerializer.SerializeToNode(value)!.AsObject(); o["id"] ??= Guid.NewGuid().ToString("N"); d[table]!.AsArray().Add(o); return o;
    }
    public static void Check(bool ok,string message,int status=400) { if(!ok) throw new DomainError(message,status); }
    public static string Required(this JsonNode n,string key,int max=200) {var v=n.S(key).Trim(); Check(v.Length>0 && v.Length<=max,$"Проверьте поле: {key}");return v;}
    public static DateOnly Day(this JsonNode n,string key) {Check(DateOnly.TryParse(n.S(key),out var date),$"Проверьте дату: {key}");return date;}
    public static DateOnly Today=>DateOnly.FromDateTime(DateTime.UtcNow);
    public static string Now=>DateTimeOffset.UtcNow.ToString("O");
    public static string Id()=>Guid.NewGuid().ToString("N");
}
