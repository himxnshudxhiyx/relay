using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Relay.Core;

// Ported from Sources/Relay/Models/Models.swift. The JSON these write is the
// same as the Mac app's, so a workspace.json moves between the two unchanged.
// Every property has a default, so a missing key decodes to it, as on the Mac.

/// A row in a params, headers, form or variables table.
public partial class KeyValue : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _description = "";
    /// <summary>Multipart only: <see cref="Value"/> is a file path.</summary>
    [ObservableProperty] private bool _isFile;
    /// <summary>Variables only: masked in the editor.</summary>
    [ObservableProperty] private bool _isSecret;

    public KeyValue() { }

    public KeyValue(string key, string value, bool enabled = true, string description = "", bool isFile = false, bool isSecret = false)
    {
        _key = key;
        _value = value;
        _enabled = enabled;
        _description = description;
        _isFile = isFile;
        _isSecret = isSecret;
    }

    [JsonIgnore] public bool IsBlank => Key.Length == 0 && Value.Length == 0;
}

// The first member of each enum is its fallback for unknown values.
public enum BodyMode { None, Json, Text, Xml, Form, Multipart, File }
public enum AuthType { None, Inherit, Bearer, Basic, ApiKey }
public enum ApiKeyLocation { Header, Query }

public static class BodyModeExtensions
{
    public static string Title(this BodyMode mode) => mode switch
    {
        BodyMode.Json => "JSON",
        BodyMode.Text => "Text",
        BodyMode.Xml => "XML",
        BodyMode.Form => "Form URL-Encoded",
        BodyMode.Multipart => "Multipart Form",
        BodyMode.File => "Binary File",
        _ => "None",
    };

    /// <summary>Short label for chips and badges.</summary>
    public static string ChipTitle(this BodyMode mode) => mode switch
    {
        BodyMode.Form => "Form",
        BodyMode.Multipart => "Multipart",
        BodyMode.File => "File",
        _ => mode.Title(),
    };

    /// <summary>The Content-Type sent when the request doesn't set one. Multipart gets its boundary from the client.</summary>
    public static string? ContentType(this BodyMode mode) => mode switch
    {
        BodyMode.Json => "application/json",
        BodyMode.Text => "text/plain",
        BodyMode.Xml => "application/xml",
        BodyMode.Form => "application/x-www-form-urlencoded",
        BodyMode.File => "application/octet-stream",
        _ => null,
    };

    /// <summary>Edited as free text rather than a table.</summary>
    public static bool IsRaw(this BodyMode mode) => mode is BodyMode.Json or BodyMode.Text or BodyMode.Xml;

    public static string Title(this AuthType type) => type switch
    {
        AuthType.Inherit => "Inherit from Parent",
        AuthType.Bearer => "Bearer Token",
        AuthType.Basic => "Basic Auth",
        AuthType.ApiKey => "API Key",
        _ => "No Auth",
    };

    public static string Title(this ApiKeyLocation location) => location == ApiKeyLocation.Header ? "Header" : "Query Param";
}

public partial class RequestBody : ObservableObject
{
    [ObservableProperty] private BodyMode _mode = BodyMode.None;
    [ObservableProperty] private string _raw = "";
    [ObservableProperty] private ObservableCollection<KeyValue> _form = [];
    [ObservableProperty] private string _filePath = "";
}

public partial class Auth : ObservableObject
{
    [ObservableProperty] private AuthType _type = AuthType.Inherit;
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private ApiKeyLocation _location = ApiKeyLocation.Header;

    public static Auth Inherit() => new();
    public static Auth NoAuth() => new() { Type = AuthType.None };
}

/// <summary>A collection's tree holds folders and requests. Stored as <c>{"folder": …}</c> or <c>{"request": …}</c>.</summary>
public abstract partial class CollectionItem : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _docs = "";
    /// <summary>Postman fields Relay doesn't edit (scripts, saved examples), kept so an export loses nothing.</summary>
    [ObservableProperty] private Dictionary<string, JsonElement> _extras = [];
}

public partial class ApiRequest : CollectionItem
{
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public const string DefaultName = "Untitled Request";

    [ObservableProperty] private string _method = "GET";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private ObservableCollection<KeyValue> _params = [];
    [ObservableProperty] private ObservableCollection<KeyValue> _headers = [];
    [ObservableProperty] private RequestBody _body = new();
    [ObservableProperty] private Auth _auth = Auth.Inherit();

    public ApiRequest() => Name = DefaultName;

    /// <summary>What New Request starts from. <c>new ApiRequest()</c> stays body-less, because imports and decoding build on it.</summary>
    public static ApiRequest Blank()
    {
        var r = new ApiRequest();
        r.Body.Mode = BodyMode.Json;
        return r;
    }
}

public partial class Folder : CollectionItem
{
    [ObservableProperty] private ObservableCollection<CollectionItem> _items = [];
    [ObservableProperty] private Auth _auth = Auth.Inherit();

    public Folder() => Name = "New Folder";
}

public partial class ApiCollection : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "New Collection";
    [ObservableProperty] private ObservableCollection<CollectionItem> _items = [];
    [ObservableProperty] private Auth _auth = Auth.NoAuth();
    [ObservableProperty] private ObservableCollection<KeyValue> _variables = [];
    [ObservableProperty] private string _docs = "";
    [ObservableProperty] private Dictionary<string, JsonElement> _extras = [];
}

public partial class ApiEnvironment : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "New Environment";
    [ObservableProperty] private ObservableCollection<KeyValue> _variables = [];
}

public partial class HistoryEntry : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private DateTime _date = DateTime.UtcNow;
    [ObservableProperty] private ApiRequest _request = new();
    [ObservableProperty] private int? _status;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private string? _error;
}

/// <summary>An open tab: the request being edited and its last saved state.</summary>
public partial class RequestTab : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    /// <summary>The collection the request is saved in; null for a scratch request.</summary>
    [ObservableProperty] private Guid? _collectionID;
    [ObservableProperty] private ApiRequest _draft = new();
    /// <summary>The last saved state, so the tab can tell it has unsaved edits.</summary>
    [ObservableProperty] private ApiRequest _original = new();
    /// <summary>Opened with a single click; the next single click reuses it until it's edited.</summary>
    [ObservableProperty] private bool _isPreview;
}

/// <summary>What's saved in workspace.json: the things you'd be upset to lose.</summary>
public class WorkspaceFile
{
    public List<ApiCollection> Collections { get; set; } = [];
    public List<ApiEnvironment> Environments { get; set; } = [];
    public Guid? ActiveEnvironmentID { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}

/// <summary>What's saved in session.json: open tabs (unsaved edits included) and sidebar state.</summary>
public class SessionFile
{
    public List<RequestTab> Tabs { get; set; } = [];
    public Guid? SelectedTabID { get; set; }
    public List<Guid> Expanded { get; set; } = [];
}
