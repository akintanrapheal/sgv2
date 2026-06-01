namespace SterlingLams.Web.Services.Odoo.OdooModels;

/// <summary>Odoo JSON-RPC request envelope</summary>
public class OdooRpcRequest
{
    public string JsonRpc { get; set; } = "2.0";
    public string Method { get; set; } = "call";
    public int Id { get; set; } = 1;
    public OdooRpcParams Params { get; set; } = new();
}

public class OdooRpcParams
{
    public string Service { get; set; } = "object";
    public string Method { get; set; } = "execute_kw";
    public object[] Args { get; set; } = Array.Empty<object>();
}

/// <summary>Odoo JSON-RPC response envelope</summary>
public class OdooRpcResponse<T>
{
    public string JsonRpc { get; set; } = string.Empty;
    public int Id { get; set; }
    public T? Result { get; set; }
    public OdooRpcError? Error { get; set; }
    public bool IsSuccess => Error == null;
}

public class OdooRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public OdooRpcErrorData? Data { get; set; }
}

public class OdooRpcErrorData
{
    public string Name { get; set; } = string.Empty;
    public string Debug { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
