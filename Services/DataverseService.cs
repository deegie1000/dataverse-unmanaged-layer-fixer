using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Net.Http.Headers;

namespace DataverseUnmanagedLayerFixer.Services;

/// <summary>
/// Service for managing Dataverse connection and basic operations.
/// </summary>
public class DataverseService : IDisposable
{
    private ServiceClient? _serviceClient;
    private HttpClient? _httpClient;
    private bool _disposed;

    public ServiceClient ServiceClient => _serviceClient ?? throw new InvalidOperationException("Not connected to Dataverse");
    public HttpClient HttpClient => _httpClient ?? throw new InvalidOperationException("HttpClient not initialized");
    public bool IsConnected => _serviceClient?.IsReady == true;

    /// <summary>
    /// Connects to Dataverse using interactive OAuth authentication.
    /// </summary>
    public bool Connect(string environmentUrl)
    {
        try
        {
            Console.WriteLine($"Connecting to: {environmentUrl}");
            Console.WriteLine("A browser window will open for authentication...");

            string connectionString = $@"
                AuthType=OAuth;
                Url={environmentUrl};
                LoginPrompt=Auto;
                RequireNewInstance=True;
                RedirectUri=http://localhost;
                AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;
                TokenCacheStorePath=./tokencache.dat";

            _serviceClient = new ServiceClient(connectionString);

            if (!_serviceClient.IsReady)
            {
                Console.WriteLine($"Connection Error: {_serviceClient.LastError}");
                return false;
            }

            InitializeHttpClient();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to Dataverse: {ex.Message}");
            return false;
        }
    }

    private void InitializeHttpClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = _serviceClient!.ConnectedOrgUriActual
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _serviceClient.CurrentAccessToken);
        _httpClient.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        _httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Retrieves multiple records using a query expression.
    /// </summary>
    public async Task<EntityCollection> RetrieveMultipleAsync(QueryExpression query)
    {
        return await Task.Run(() => ServiceClient.RetrieveMultiple(query));
    }

    /// <summary>
    /// Retrieves a single record by ID.
    /// </summary>
    public async Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet)
    {
        return await Task.Run(() => ServiceClient.Retrieve(entityName, id, columnSet));
    }

    /// <summary>
    /// Executes an organization request.
    /// </summary>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
    {
        return await Task.Run(() => ServiceClient.Execute(request));
    }

    /// <summary>
    /// Retrieves all records with paging support.
    /// </summary>
    public async Task<List<Entity>> RetrieveAllAsync(QueryExpression query, Action<int, int>? progressCallback = null)
    {
        var allEntities = new List<Entity>();
        query.PageInfo ??= new PagingInfo { Count = 5000, PageNumber = 1 };

        int pageNumber = 1;
        while (true)
        {
            progressCallback?.Invoke(pageNumber, allEntities.Count);

            var results = await RetrieveMultipleAsync(query);
            allEntities.AddRange(results.Entities);

            if (results.MoreRecords)
            {
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = results.PagingCookie;
                pageNumber++;
            }
            else
            {
                break;
            }
        }

        return allEntities;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _serviceClient?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
