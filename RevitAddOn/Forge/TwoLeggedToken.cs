using System;
using System.Threading.Tasks;
using Autodesk.Forge;
using Autodesk.Forge.Client;

namespace Revit.SDK.Samples.CloudAPISample.CS.APS
{
    /// <summary>
    /// Handles 2-legged authentication with Autodesk Platform Services (APS)
    /// This enables automated token generation without user interaction
    /// </summary>
    public class TwoLeggedToken
    {
        private static bool _tokenInitialized = false;
        private static TwoLeggedApi _twoLeggedApi = new TwoLeggedApi();
        private static string _token = null;
        private static DateTime _tokenExpiry = DateTime.MinValue;

        // Scopes suitable for data management tasks
        private static readonly Scope[] _scopes = new Scope[] {
            Scope.DataRead,
            Scope.DataWrite,
            Scope.DataCreate,
            Scope.BucketCreate,
            Scope.BucketRead
        };

        /// <summary>
        /// Client ID from environment variable
        /// </summary>
        public static string APS_CLIENT_ID = Environment.GetEnvironmentVariable("APS_CLIENT_ID", EnvironmentVariableTarget.User) ?? "your_client_id";

        /// <summary>
        /// Client Secret from environment variable
        /// </summary>
        public static string APS_CLIENT_SECRET = Environment.GetEnvironmentVariable("APS_CLIENT_SECRET", EnvironmentVariableTarget.User) ?? "your_client_secret";

        /// <summary>
        /// Delegate for token generation callback
        /// </summary>
        public delegate void TokenGeneratedDelegate();

        /// <summary>
        /// Get the current token status
        /// </summary>
        public static bool TokenInitialized
        {
            get { return _tokenInitialized; }
        }

        /// <summary>
        /// Generate a new token asynchronously
        /// </summary>
        /// <param name="callback">Optional callback to execute after token generation</param>
        public static async Task GenerateTokenAsync(TokenGeneratedDelegate callback = null)
        {
            try
            {
                // Get the authentication token using the client credentials flow
                ApiResponse<dynamic> response = await _twoLeggedApi.AuthenticateAsyncWithHttpInfo(
                    APS_CLIENT_ID,
                    APS_CLIENT_SECRET,
                    oAuthConstants.CLIENT_CREDENTIALS,
                    _scopes);

                if (response.StatusCode != 200 || response.Data == null)
                {
                    throw new Exception("Failed to authenticate. Status code: " + response.StatusCode);
                }

                // Extract the access token
                _token = response.Data.access_token;

                // Calculate token expiry (default token lifetime is typically ~1 hour)
                int expiresIn = response.Data.expires_in;
                _tokenExpiry = DateTime.Now.AddSeconds(expiresIn);

                _tokenInitialized = true;

                // Execute callback if provided
                callback?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error generating 2-legged token: " + ex.Message);
                _tokenInitialized = false;
                throw;
            }
        }

        /// <summary>
        /// Gets a token, generating a new one if the current one is expired or not initialized
        /// </summary>
        /// <returns>The access token</returns>
        public static async Task<string> GetTokenAsync()
        {
            // Check if token is expired or not initialized (with 5-minute buffer)
            if (_token == null || DateTime.Now > _tokenExpiry.AddMinutes(-5))
            {
                await GenerateTokenAsync();
            }

            return _token;
        }

        /// <summary>
        /// Gets a token synchronously, blocking until the token is available
        /// </summary>
        /// <returns>The access token</returns>
        public static string GetToken()
        {
            // If token is expired or not initialized, generate a new one
            if (_token == null || DateTime.Now > _tokenExpiry.AddMinutes(-5))
            {
                try
                {
                    Task.Run(async () => { await GenerateTokenAsync(); }).Wait();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error in GetToken: " + ex.Message);
                    throw;
                }
            }

            return _token;
        }
    }
}