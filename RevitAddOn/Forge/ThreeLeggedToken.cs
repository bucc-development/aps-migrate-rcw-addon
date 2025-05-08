using System;
using System.Text;
using System.Net;
using Autodesk.Forge;
using Autodesk.Forge.Client;
using System.Threading;
using System.Threading.Tasks;

namespace Revit.SDK.Samples.CloudAPISample.CS.APS
{
    class ThreeLeggedToken
    {
        private static bool _threeLeggedTokenInitialized = false;
        private static ThreeLeggedApi _threeLeggedApi = new ThreeLeggedApi();
        private static string _threeLeggedToken = null;
        private static DateTime _dt;
        private static HttpListener _httpListener = null;
        private static readonly object _lockObject = new object();
        private static bool _authInProgress = false;

        private static readonly Scope[] _scope = new Scope[] { Scope.DataRead, Scope.DataWrite };

        private static string APS_CLIENT_ID = Environment.GetEnvironmentVariable("APS_CLIENT_ID", EnvironmentVariableTarget.User) ?? "your_client_id";
        private static string APS_CLIENT_SECRET = Environment.GetEnvironmentVariable("APS_CLIENT_SECRET", EnvironmentVariableTarget.User) ?? "your_client_secret";
        private static string APS_CALLBACK = Environment.GetEnvironmentVariable("APS_CALLBACK", EnvironmentVariableTarget.User) ?? "your_callback";

        internal delegate void NewBearerDelegate();

        public class TokenData
        {
            public NewBearerDelegate callback = null;
            public dynamic control = null;
        }

        /// <summary>
        /// Generates a new authentication token using 3-legged OAuth flow
        /// </summary>
        public static void GenerateToken(TokenData cbData)
        {
            lock (_lockObject)
            {
                if (_authInProgress)
                {
                    Console.WriteLine("Authentication already in progress");
                    return;
                }
                _authInProgress = true;
            }

            _threeLeggedTokenInitialized = false;

            try
            {
                if (!HttpListener.IsSupported)
                {
                    throw new Exception("HttpListener is not supported on this platform");
                }

                // Clean up any existing listener
                CleanupListener();

                // Create a new listener
                _httpListener = new HttpListener();

                // Fix the URL prefix issue - ensure proper formatting
                string listenerPrefix = FixListenerPrefix(APS_CALLBACK);
                _httpListener.Prefixes.Add(listenerPrefix);

                Console.WriteLine($"Starting listener on: {listenerPrefix}");
                _httpListener.Start();

                // Start listening for the callback
                _httpListener.BeginGetContext(AuthCallbackHandler, cbData);

                // Generate the authorization URL and open browser
                string oauthUrl = _threeLeggedApi.Authorize(APS_CLIENT_ID, oAuthConstants.CODE, APS_CALLBACK, _scope);
                Console.WriteLine($"Opening browser to: {oauthUrl}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(oauthUrl));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in GenerateToken: {e.Message}");
                CleanupListener();
                _authInProgress = false;
                throw;
            }
        }

        /// <summary>
        /// Fixes the listener prefix to ensure proper formatting
        /// </summary>
        private static string FixListenerPrefix(string callbackUrl)
        {
            // Parse the URL to ensure correct formatting
            Uri uri = new Uri(callbackUrl);

            // For localhost, we need to use "+" to listen on all interfaces
            string host = uri.Host.Replace("localhost", "+");

            // Ensure the path ends with a single slash
            string path = uri.AbsolutePath;
            if (!path.EndsWith("/"))
            {
                path += "/";
            }

            // Construct the proper listener prefix
            string listenerPrefix = $"{uri.Scheme}://{host}:{uri.Port}{path}";

            // Ensure no double slashes in the path
            listenerPrefix = listenerPrefix.Replace("//", "/");
            listenerPrefix = listenerPrefix.Replace(":/", "://"); // Fix protocol separator

            return listenerPrefix;
        }

        /// <summary>
        /// Handles the OAuth callback asynchronously
        /// </summary>
        private static async void AuthCallbackHandler(IAsyncResult ar)
        {
            HttpListenerContext context = null;
            TokenData tokenData = (TokenData)ar.AsyncState;

            try
            {
                // Get the HTTP context
                context = _httpListener.EndGetContext(ar);

                // Extract the authorization code
                string code = context.Request.QueryString[oAuthConstants.CODE];
                Console.WriteLine($"Received authorization code: {!string.IsNullOrEmpty(code)}");

                // Send response to browser
                await SendBrowserResponse(context.Response, code != null);

                // Exchange code for token if we got one
                if (!string.IsNullOrEmpty(code))
                {
                    await ExchangeCodeForToken(code);

                    // Invoke callback on UI thread if provided
                    if (tokenData?.control != null && tokenData.callback != null)
                    {
                        tokenData.control.Dispatcher.Invoke(tokenData.callback);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in AuthCallbackHandler: {ex.Message}");
                _threeLeggedTokenInitialized = false;

                // Try to send error response to browser
                if (context != null)
                {
                    try
                    {
                        await SendBrowserResponse(context.Response, false, ex.Message);
                    }
                    catch { }
                }
            }
            finally
            {
                // Clean up the listener and reset auth state
                CleanupListener();
                _authInProgress = false;
            }
        }

        /// <summary>
        /// Exchanges the authorization code for an access token
        /// </summary>
        private static async Task ExchangeCodeForToken(string code)
        {
            try
            {
                // Call the API to exchange code for token
                ApiResponse<dynamic> bearer = await _threeLeggedApi.GettokenAsyncWithHttpInfo(
                    APS_CLIENT_ID,
                    APS_CLIENT_SECRET,
                    oAuthConstants.AUTHORIZATION_CODE,
                    code,
                    APS_CALLBACK);

                if (bearer.StatusCode != 200 || bearer.Data == null)
                {
                    throw new Exception($"Token exchange failed with status {bearer.StatusCode}");
                }

                // Store the token and mark as initialized
                _threeLeggedToken = bearer.Data.access_token;
                _dt = DateTime.Now;
                _threeLeggedTokenInitialized = true;

                Console.WriteLine("Successfully obtained access token");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exchanging code for token: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sends a response to the browser
        /// </summary>
        private static async Task SendBrowserResponse(HttpListenerResponse response, bool success, string errorMessage = null)
        {
            string responseString;

            if (success)
            {
                responseString = @"
                    <html>
                    <head><title>Authentication Successful</title></head>
                    <body>
                        <h1>Authentication Successful!</h1>
                        <p>You can now close this window and return to the application.</p>
                        <script>setTimeout(function() { window.close(); }, 2000);</script>
                    </body>
                    </html>";
            }
            else
            {
                responseString = $@"
                    <html>
                    <head><title>Authentication Failed</title></head>
                    <body>
                        <h1>Authentication Failed</h1>
                        <p>Error: {errorMessage ?? "Unknown error occurred"}</p>
                        <p>Please close this window and try again.</p>
                    </body>
                    </html>";
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = success ? 200 : 400;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        /// <summary>
        /// Cleans up the HTTP listener
        /// </summary>
        private static void CleanupListener()
        {
            if (_httpListener != null)
            {
                try
                {
                    if (_httpListener.IsListening)
                    {
                        _httpListener.Stop();
                    }
                    _httpListener.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error cleaning up listener: {ex.Message}");
                }
                finally
                {
                    _httpListener = null;
                }
            }
        }

        /// <summary>
        /// Gets the current token, generating a new one if necessary
        /// </summary>
        public static string GetToken()
        {
            if (_threeLeggedToken == null || ((DateTime.Now - _dt) > TimeSpan.FromMinutes(30)))
            {
                GenerateToken(null);

                // Wait for token to be initialized
                int waitCount = 0;
                while (!TokenInitialized && waitCount < 60) // 2 minute timeout
                {
                    Thread.Sleep(2000);
                    waitCount++;
                }

                if (!TokenInitialized)
                {
                    throw new Exception("Failed to obtain authentication token within timeout period");
                }

                _dt = DateTime.Now;
                return _threeLeggedToken;
            }
            else
            {
                return _threeLeggedToken;
            }
        }

        /// <summary>
        /// Gets whether the token has been initialized
        /// </summary>
        public static bool TokenInitialized
        {
            get { return _threeLeggedTokenInitialized; }
        }
    }
}