// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Dispatcher;
using System.Web.Http.Internal;
using System.Web.Http.ModelBinding;
using System.Web.Http.Properties;
using System.Web.Http.Routing;
using System.Web.Http.ValueProviders;
using ApiActionSelection.System.Collections.Generic;
using ApiActionSelection.System.Collections.ObjectModel;
using ApiActionSelection.System.Web.Http.Internal;
using ApiActionSelection.System.Web.Http.Routing;
using ApiActionSelection.System.Web.Http.ValueProviders;

namespace ApiActionSelection.System.Web.Http.Controllers
{
    /// <summary>
    /// Reflection based action selector.
    /// We optimize for the case where we have an <see cref="ApiControllerActionSelector"/> instance per <see cref="HttpControllerDescriptor"/>
    /// instance but can support cases where there are many <see cref="HttpControllerDescriptor"/> instances for one
    /// <see cref="ApiControllerActionSelector"/> as well. In the latter case the lookup is slightly slower because it goes through
    /// the <see cref="P:HttpControllerDescriptor.Properties"/> dictionary.
    /// </summary>
    public class ApiActionSelector : IHttpActionSelector
    {
       
        private ActionSelectorCacheItem _fastCache;
        private readonly object _cacheKey = new object();

        public virtual HttpActionDescriptor SelectAction(HttpControllerContext controllerContext)
        {
            if (controllerContext == null)
            {
                throw new ArgumentNullException("controllerContext");  //Error.ArgumentNull("controllerContext");
            }

            ActionSelectorCacheItem internalSelector = GetInternalSelector(controllerContext.ControllerDescriptor);
            return internalSelector.SelectAction(controllerContext);
        }

        public virtual ILookup<string, HttpActionDescriptor> GetActionMapping(HttpControllerDescriptor controllerDescriptor)
        {
            if (controllerDescriptor == null)
            {
                throw new ArgumentNullException("controllerDescriptor"); // Error.ArgumentNull("controllerDescriptor");
            }

            ActionSelectorCacheItem internalSelector = GetInternalSelector(controllerDescriptor);
            return internalSelector.GetActionMapping();
        }

        /// <summary>
        /// This is called in case of ambiguous action scenario
        /// return null if you cannot resolve the actions
        /// </summary>
        protected virtual ReflectedHttpActionDescriptor ResolveAmbiguousActions(HttpControllerContext context,
                IEnumerable<ReflectedHttpActionDescriptor> actionDescriptors)
        {
            return null;
        }

        private ActionSelectorCacheItem GetInternalSelector(HttpControllerDescriptor controllerDescriptor)
        {
            // Performance-sensitive

            // First check in the local fast cache and if not a match then look in the broader 
            // HttpControllerDescriptor.Properties cache
            if (_fastCache == null)
            {
                ActionSelectorCacheItem selector = new ActionSelectorCacheItem(controllerDescriptor, ResolveAmbiguousActions);
                Interlocked.CompareExchange(ref _fastCache, selector, null);
                return selector;
            }
            else if (_fastCache.HttpControllerDescriptor == controllerDescriptor)
            {
                // If the key matches and we already have the delegate for creating an instance then just execute it
                return _fastCache;
            }
            else
            {
                // If the key doesn't match then lookup/create delegate in the HttpControllerDescriptor.Properties for
                // that HttpControllerDescriptor instance
                object cacheValue;
                if (controllerDescriptor.Properties.TryGetValue(_cacheKey, out cacheValue))
                {
                    return (ActionSelectorCacheItem)cacheValue;
                }
                // Race condition on initialization has no side effects
                ActionSelectorCacheItem selector = new ActionSelectorCacheItem(controllerDescriptor, ResolveAmbiguousActions);
                controllerDescriptor.Properties.TryAdd(_cacheKey, selector);
                return selector;
            }
        }

        // All caching is in a dedicated cache class, which may be optionally shared across selector instances.
        // Make this a private nested class so that nobody else can conflict with our state.
        // Cache is initialized during ctor on a single thread.
        private class ActionSelectorCacheItem
        {
            private readonly HttpControllerDescriptor _controllerDescriptor;

            // Includes action descriptors for actions with and without route attributes.
            private readonly CandidateAction[] _combinedCandidateActions;

            // Includes action descriptors only for actions accessible via standard routing (without route attributes).
            private readonly CandidateAction[] _standardCandidateActions;

            private readonly IDictionary<ReflectedHttpActionDescriptor, string[]> _actionParameterNames = new Dictionary<ReflectedHttpActionDescriptor, string[]>();

            // Includes action descriptors for actions with and without route attributes.
            private readonly ILookup<string, ReflectedHttpActionDescriptor> _combinedActionNameMapping;

            // Includes action descriptors only for actions accessible via standard routing (without route attributes).
            private readonly ILookup<string, ReflectedHttpActionDescriptor> _standardActionNameMapping;

            // Selection commonly looks up an action by verb.
            // Cache this mapping. These caches are completely optional and we still behave correctly if we cache miss.
            // We can adjust the specific set we cache based on profiler information.
            // Conceptually, this set of caches could be a HttpMethod --> ReflectedHttpActionDescriptor[].
            // - Beware that HttpMethod has a very slow hash function (it does case-insensitive string hashing). So don't use Dict.
            // - there are unbounded number of http methods, so make sure the cache doesn't grow indefinitely.
            // - we can build the cache at startup and don't need to continually add to it.
            private readonly HttpMethod[] _cacheListVerbKinds = new HttpMethod[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post };

            private readonly CandidateAction[][] _cacheListVerbs;
            private readonly Func<HttpControllerContext, IEnumerable<ReflectedHttpActionDescriptor>, ReflectedHttpActionDescriptor> _ambiguousResolver;

            public ActionSelectorCacheItem(HttpControllerDescriptor controllerDescriptor, 
                Func<HttpControllerContext, IEnumerable<ReflectedHttpActionDescriptor>, ReflectedHttpActionDescriptor> ambiguousResolver)
            {
                _ambiguousResolver = ambiguousResolver;
                Contract.Assert(controllerDescriptor != null);

                // Initialize the cache entirely in the ctor on a single thread.
                _controllerDescriptor = controllerDescriptor;

                MethodInfo[] allMethods = _controllerDescriptor.ControllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                MethodInfo[] validMethods = Array.FindAll(allMethods, IsValidActionMethod);

                _combinedCandidateActions = new CandidateAction[validMethods.Length];
                for (int i = 0; i < validMethods.Length; i++)
                {
                    MethodInfo method = validMethods[i];
                    ReflectedHttpActionDescriptor actionDescriptor = new ReflectedHttpActionDescriptor(_controllerDescriptor, method);
                    _combinedCandidateActions[i] = new CandidateAction
                    {
                        ActionDescriptor = actionDescriptor
                    };
                    HttpActionBinding actionBinding = actionDescriptor.ActionBinding;

                    // Building an action parameter name mapping to compare against the URI parameters coming from the request. Here we only take into account required parameters that are simple types and come from URI.
                    _actionParameterNames.Add(
                        actionDescriptor,
                        actionBinding.ParameterBindings
                            .Where(binding => !binding.Descriptor.IsOptional && TypeHelper.CanConvertFromString(binding.Descriptor.ParameterType) && binding.WillReadUri())
                            .Select(binding => binding.Descriptor.Prefix ?? binding.Descriptor.ParameterName).ToArray());
                }

                if (controllerDescriptor.GetCustomAttributes<IHttpRouteInfoProvider>(inherit: false).Any())
                {
                    // The controller has an attribute route; no actions are accessible via standard routing.
                    _standardCandidateActions = new CandidateAction[0];
                }
                else
                {
                    // The controller does not have an attribute route; some actions may be accessible via standard
                    // routing.
                    List<CandidateAction> standardCandidateActions = new List<CandidateAction>();

                    for (int i = 0; i < _combinedCandidateActions.Length; i++)
                    {
                        CandidateAction candidate = _combinedCandidateActions[i];
                        // Allow standard routes access inherited actions or actions without Route attributes.
                        if (candidate.ActionDescriptor.MethodInfo.DeclaringType != controllerDescriptor.ControllerType ||
                            !candidate.ActionDescriptor.GetCustomAttributes<IHttpRouteInfoProvider>(inherit: false).Any())
                        {
                            standardCandidateActions.Add(candidate);
                        }
                    }

                    _standardCandidateActions = standardCandidateActions.ToArray();
                }

                _combinedActionNameMapping = _combinedCandidateActions.Select(c => c.ActionDescriptor).ToLookup(actionDesc => actionDesc.ActionName, StringComparer.OrdinalIgnoreCase);
                _standardActionNameMapping = _standardCandidateActions.Select(c => c.ActionDescriptor).ToLookup(actionDesc => actionDesc.ActionName, StringComparer.OrdinalIgnoreCase);

                // Bucket the action descriptors by common verbs.
                int len = _cacheListVerbKinds.Length;
                _cacheListVerbs = new CandidateAction[len][];
                for (int i = 0; i < len; i++)
                {
                    _cacheListVerbs[i] = FindActionsForVerbWorker(_cacheListVerbKinds[i]);
                }
            }

            public HttpControllerDescriptor HttpControllerDescriptor
            {
                get { return _controllerDescriptor; }
            }

            public HttpActionDescriptor SelectAction(HttpControllerContext controllerContext)
            {
                List<CandidateActionWithParams> selectedCandidates = FindMatchingActions(controllerContext);

                switch (selectedCandidates.Count)
                {
                    case 0:
                        throw new HttpResponseException(CreateSelectionError(controllerContext));
                    case 1:
                        ElevateRouteData(controllerContext, selectedCandidates[0]);
                        return selectedCandidates[0].ActionDescriptor;
                    default:
                        if (_ambiguousResolver != null)
                        {
                            var resolved = _ambiguousResolver(controllerContext,
                                selectedCandidates.Select(x => x.ActionDescriptor));

                            if (resolved != null)
                            {
                                ElevateRouteData(controllerContext, selectedCandidates.First(x => x.ActionDescriptor == resolved));
                                return resolved;
                            }
                        }

                        // Throws exception because multiple actions match the request
                        string ambiguityList = CreateAmbiguousMatchList(selectedCandidates);
                        throw new InvalidOperationException("Ambiguous Match"); //Error.InvalidOperation(SRResources.ApiControllerActionSelector_AmbiguousMatch, ambiguityList);
                }
            }

            private static void ElevateRouteData(HttpControllerContext controllerContext, CandidateActionWithParams selectedCandidate)
            {
                controllerContext.RouteData = selectedCandidate.RouteDataSource;
            }

            // Find all actions on this controller that match the request. 
            // if ignoreVerbs = true, then don't filter actions based on mismatching Http verb. This is useful for detecting 404/405. 
            private List<CandidateActionWithParams> FindMatchingActions(HttpControllerContext controllerContext, bool ignoreVerbs = false)
            {
                // If matched with direct route?
                IHttpRouteData routeData = controllerContext.RouteData;
                IEnumerable<IHttpRouteData> subRoutes = routeData.GetSubRoutes();

                IEnumerable<CandidateActionWithParams> actionsWithParameters = (subRoutes == null) ?
                    GetInitialCandidateWithParameterListForRegularRoutes(controllerContext, ignoreVerbs) :
                    GetInitialCandidateWithParameterListForDirectRoutes(controllerContext, subRoutes, ignoreVerbs);

                // Make sure the action parameter matches the route and query parameters.
                List<CandidateActionWithParams> actionsFoundByParams = FindActionMatchRequiredRouteAndQueryParameters(actionsWithParameters);

                List<CandidateActionWithParams> orderCandidates = RunOrderFilter(actionsFoundByParams);
                List<CandidateActionWithParams> precedenceCandidates = RunPrecedenceFilter(orderCandidates);

                // Overload resolution logic is applied when needed.
                List<CandidateActionWithParams> selectedCandidates = FindActionMatchMostRouteAndQueryParameters(precedenceCandidates);

                return selectedCandidates;
            }

            // Selection error. Caller has already determined the request is an error, and now we need to provide the best error message.
            // If there's another verb that could satisfy this URL, then return 405.
            // Else return 404.
            [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Caller is responsible for disposing of response instance.")]
            private HttpResponseMessage CreateSelectionError(HttpControllerContext controllerContext)
            {
                // Check for 405.  
                List<CandidateActionWithParams> actionsFoundByParams = FindMatchingActions(controllerContext, ignoreVerbs: true);

                if (actionsFoundByParams.Count > 0)
                {
                    return Create405Response(controllerContext, actionsFoundByParams);
                }

                // Throws HttpResponseException with NotFound status because no action matches the request
                return CreateActionNotFoundResponse(controllerContext);
            }

            // Create a 405 error response with proper headers and message string. 
            [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Caller is responsible for disposing of response instance.")]
            private static HttpResponseMessage Create405Response(HttpControllerContext controllerContext, IEnumerable<CandidateActionWithParams> allowedCandidates)
            {
                HttpMethod incomingMethod = controllerContext.Request.Method;
                HttpResponseMessage response = controllerContext.Request.CreateErrorResponse(
                    HttpStatusCode.MethodNotAllowed,
                    "Http Method not supported"); // Error.Format(SRResources.ApiControllerActionSelector_HttpMethodNotSupported, incomingMethod));

                // 405 must include an Allow content-header with the allowable methods.
                // See: https://tools.ietf.org/html/rfc2616#section-14.7
                HashSet<HttpMethod> methods = new HashSet<HttpMethod>();
                foreach (var candidate in allowedCandidates)
                {
                    methods.UnionWith(candidate.ActionDescriptor.SupportedHttpMethods);
                }
                foreach (var method in methods)
                {
                    response.Content.Headers.Allow.Add(method.ToString());
                }

                return response;
            }

            // Create a 404
            private HttpResponseMessage CreateActionNotFoundResponse(HttpControllerContext controllerContext)
            {
                return controllerContext.Request.CreateErrorResponse(
                    HttpStatusCode.NotFound,
                    "Action not found in " + _controllerDescriptor.ControllerName);
                //"Error.Format(SRResources.ApiControllerActionSelector_ActionNotFound, _controllerDescriptor.ControllerName)");
            }

            // Create a 404, including the name of the action we were looking for. 
            // This overload includes the action name. 
            private HttpResponseMessage CreateActionNotFoundResponse(HttpControllerContext controllerContext, string actionName)
            {
                return controllerContext.Request.CreateErrorResponse(
                    HttpStatusCode.NotFound,
                    "Action Name not found in " + _controllerDescriptor.ControllerName); //,
                //Error.Format(SRResources.ApiControllerActionSelector_ActionNameNotFound, _controllerDescriptor.ControllerName, actionName));
            }

            // Call for direct routes. 
            private static List<CandidateActionWithParams> GetInitialCandidateWithParameterListForDirectRoutes(HttpControllerContext controllerContext, IEnumerable<IHttpRouteData> subRoutes, bool ignoreVerbs)
            {
                HttpRequestMessage request = controllerContext.Request;
                HttpMethod incomingMethod = controllerContext.Request.Method;

                var queryNameValuePairs = request.GetQueryNameValuePairs();

                List<CandidateActionWithParams> candidateActionWithParams = new List<CandidateActionWithParams>();

                foreach (IHttpRouteData subRouteData in subRoutes)
                {
                    // Each route may have different route parameters.
                    ISet<string> combinedParameterNames = GetCombinedParameterNames(queryNameValuePairs, subRouteData.Values);

                    CandidateAction[] candidates = subRouteData.Route.GetDirectRouteCandidates();

                    string actionName;
                    subRouteData.Values.TryGetValue(RouteKeys.ActionKey, out actionName);

                    foreach (var candidate in candidates)
                    {
                        if ((actionName == null) || candidate.MatchName(actionName))
                        {
                            if (ignoreVerbs || candidate.MatchVerb(incomingMethod))
                            {
                                candidateActionWithParams.Add(new CandidateActionWithParams(candidate, combinedParameterNames, subRouteData));
                            }
                        }
                    }
                }
                return candidateActionWithParams;
            }

            // Call for non-direct routes
            private IEnumerable<CandidateActionWithParams> GetInitialCandidateWithParameterListForRegularRoutes(HttpControllerContext controllerContext, bool ignoreVerbs = false)
            {
                CandidateAction[] candidates = GetInitialCandidateList(controllerContext, ignoreVerbs);
                return GetCandidateActionsWithBindings(controllerContext, candidates);
            }

            private CandidateAction[] GetInitialCandidateList(HttpControllerContext controllerContext, bool ignoreVerbs = false)
            {
                // Initial candidate list is determined by:
                // - Direct route?
                // - {action} value?
                // - ignore verbs?
                string actionName;

                HttpMethod incomingMethod = controllerContext.Request.Method;
                IHttpRouteData routeData = controllerContext.RouteData;

                Contract.Assert(routeData.GetSubRoutes() == null, "Should not be called on a direct route");
                CandidateAction[] candidates;

                if (routeData.Values.TryGetValue(RouteKeys.ActionKey, out actionName))
                {
                    // We have an explicit {action} value, do traditional binding. Just lookup by actionName
                    ReflectedHttpActionDescriptor[] actionsFoundByName = _standardActionNameMapping[actionName].ToArray();

                    // Throws HttpResponseException with NotFound status because no action matches the Name
                    if (actionsFoundByName.Length == 0)
                    {
                        throw new HttpResponseException(CreateActionNotFoundResponse(controllerContext, actionName));
                    }

                    CandidateAction[] candidatesFoundByName = new CandidateAction[actionsFoundByName.Length];

                    for (int i = 0; i < actionsFoundByName.Length; i++)
                    {
                        candidatesFoundByName[i] = new CandidateAction
                        {
                            ActionDescriptor = actionsFoundByName[i]
                        };
                    }

                    if (ignoreVerbs)
                    {
                        candidates = candidatesFoundByName;
                    }
                    else
                    {
                        candidates = FilterIncompatibleVerbs(incomingMethod, candidatesFoundByName);
                    }
                }
                else
                {
                    if (ignoreVerbs)
                    {
                        candidates = _standardCandidateActions;
                    }
                    else
                    {
                        // No direct routing or {action} parameter, infer it from the verb.
                        candidates = FindActionsForVerb(incomingMethod);
                    }
                }

                return candidates;
            }

            private static CandidateAction[] FilterIncompatibleVerbs(HttpMethod incomingMethod, CandidateAction[] candidatesFoundByName)
            {
                return candidatesFoundByName.Where(candidate => candidate.ActionDescriptor.SupportedHttpMethods.Contains(incomingMethod)).ToArray();
            }

            public ILookup<string, HttpActionDescriptor> GetActionMapping()
            {
                return new LookupAdapter() { Source = _combinedActionNameMapping };
            }

            // Get a non-null set that combines both the route and query parameters. 
            private static ISet<string> GetCombinedParameterNames(IEnumerable<KeyValuePair<string, string>> queryNameValuePairs, IDictionary<string, object> routeValues)
            {
                HashSet<string> routeParameterNames = new HashSet<string>(routeValues.Keys, StringComparer.OrdinalIgnoreCase);
                routeParameterNames.Remove(RouteKeys.ControllerKey);
                routeParameterNames.Remove(RouteKeys.ActionKey);

                var combinedParameterNames = new HashSet<string>(routeParameterNames, StringComparer.OrdinalIgnoreCase);
                if (queryNameValuePairs != null)
                {
                    foreach (var queryNameValuePair in queryNameValuePairs)
                    {
                        combinedParameterNames.Add(queryNameValuePair.Key);
                    }
                }
                return combinedParameterNames;
            }

            private List<CandidateActionWithParams> FindActionMatchRequiredRouteAndQueryParameters(IEnumerable<CandidateActionWithParams> candidatesFound)
            {
                List<CandidateActionWithParams> matches = new List<CandidateActionWithParams>();

                foreach (var candidate in candidatesFound)
                {
                    ReflectedHttpActionDescriptor descriptor = candidate.ActionDescriptor;
                    if (IsSubset(_actionParameterNames[descriptor], candidate.CombinedParameterNames))
                    {
                        matches.Add(candidate);
                    }
                }

                return matches;
            }

            private List<CandidateActionWithParams> FindActionMatchMostRouteAndQueryParameters(List<CandidateActionWithParams> candidatesFound)
            {
                if (candidatesFound.Count > 1)
                {
                    // select the results that match the most number of required parameters
                    return candidatesFound
                        .GroupBy(candidate => _actionParameterNames[candidate.ActionDescriptor].Length)
                        .OrderByDescending(g => g.Key)
                        .First()
                        .ToList();
                }

                return candidatesFound;
            }

            // Given a list of candidate actions, return a parallel list that includes the parameter information. 
            // This is used for regular routing where all candidates come from a single route, so they all share the same route parameter names. 
            private static CandidateActionWithParams[] GetCandidateActionsWithBindings(HttpControllerContext controllerContext, CandidateAction[] candidatesFound)
            {
                HttpRequestMessage request = controllerContext.Request;
                var queryNameValuePairs = request.GetQueryNameValuePairs();
                IHttpRouteData routeData = controllerContext.RouteData;
                IDictionary<string, object> routeValues = routeData.Values;
                ISet<string> combinedParameterNames = GetCombinedParameterNames(queryNameValuePairs, routeValues);

                CandidateActionWithParams[] candidatesWithParams = Array.ConvertAll(candidatesFound, candidate => new CandidateActionWithParams(candidate, combinedParameterNames, routeData));
                return candidatesWithParams;
            }

            private static bool IsSubset(string[] actionParameters, ISet<string> routeAndQueryParameters)
            {
                foreach (string actionParameter in actionParameters)
                {
                    if (!routeAndQueryParameters.Contains(actionParameter))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static List<CandidateActionWithParams> RunOrderFilter(List<CandidateActionWithParams> candidatesFound)
            {
                if (candidatesFound.Count == 0)
                {
                    return candidatesFound;
                }
                int minOrder = candidatesFound.Min(c => c.CandidateAction.Order);
                return candidatesFound.Where(c => c.CandidateAction.Order == minOrder).AsList();
            }

            private static List<CandidateActionWithParams> RunPrecedenceFilter(List<CandidateActionWithParams> candidatesFound)
            {
                if (candidatesFound.Count == 0)
                {
                    return candidatesFound;
                }
                decimal highestPrecedence = candidatesFound.Min(c => c.CandidateAction.Precedence);
                return candidatesFound.Where(c => c.CandidateAction.Precedence == highestPrecedence).AsList();
            }

            // This is called when we don't specify an Action name
            // Get list of actions that match a given verb. This can match by name or IActionHttpMethodSelecto
            private CandidateAction[] FindActionsForVerb(HttpMethod verb)
            {
                // Check cache for common verbs.
                for (int i = 0; i < _cacheListVerbKinds.Length; i++)
                {
                    // verb selection on common verbs is normalized to have object reference identity.
                    // This is significantly more efficient than comparing the verbs based on strings.
                    if (Object.ReferenceEquals(verb, _cacheListVerbKinds[i]))
                    {
                        return _cacheListVerbs[i];
                    }
                }

                // General case for any verbs.
                return FindActionsForVerbWorker(verb);
            }

            // This is called when we don't specify an Action name
            // Given all the standard actions on the controller, filter it to ones that match a given verb.
            private CandidateAction[] FindActionsForVerbWorker(HttpMethod verb)
            {
                return FindActionsForVerbWorker(verb, _standardCandidateActions);
            }

            // Given a list of actions, filter it to ones that match a given verb. This can match by name or IActionHttpMethodSelector.
            // Since this list is fixed for a given verb type, it can be pre-computed and cached.
            // This function should not do caching. It's the helper that builds the caches.
            private static CandidateAction[] FindActionsForVerbWorker(HttpMethod verb, CandidateAction[] candidates)
            {
                List<CandidateAction> listCandidates = new List<CandidateAction>();

                FindActionsForVerbWorker(verb, candidates, listCandidates);

                return listCandidates.ToArray();
            }

            // Adds to existing list rather than send back as a return value.
            private static void FindActionsForVerbWorker(HttpMethod verb, CandidateAction[] candidates, List<CandidateAction> listCandidates)
            {
                foreach (CandidateAction candidate in candidates)
                {
                    if (candidate.ActionDescriptor != null && candidate.ActionDescriptor.SupportedHttpMethods.Contains(verb))
                    {
                        listCandidates.Add(candidate);
                    }
                }
            }

            private static string CreateAmbiguousMatchList(IEnumerable<CandidateActionWithParams> ambiguousCandidates)
            {
                StringBuilder exceptionMessageBuilder = new StringBuilder();
                foreach (CandidateActionWithParams candidate in ambiguousCandidates)
                {
                    ReflectedHttpActionDescriptor descriptor = candidate.ActionDescriptor;
                    MethodInfo methodInfo = descriptor.MethodInfo;

                    exceptionMessageBuilder.AppendLine();
                    //exceptionMessageBuilder.Append(Error.Format(
                    //SRResources.ActionSelector_AmbiguousMatchType,
                    //methodInfo, methodInfo.DeclaringType.FullName));
                }

                return exceptionMessageBuilder.ToString();
            }

            private static bool IsValidActionMethod(MethodInfo methodInfo)
            {
                if (methodInfo.IsSpecialName)
                {
                    // not a normal method, e.g. a constructor or an event
                    return false;
                }

                if (methodInfo.GetBaseDefinition().DeclaringType.IsAssignableFrom(TypeHelper.ApiControllerType))
                {
                    // is a method on Object, IHttpController, ApiController
                    return false;
                }

                if (methodInfo.GetCustomAttribute<NonActionAttribute>() != null)
                {
                    return false;
                }

                return true;
            }
        }

        // Associate parameter (route and query) with each action. 
        // For regular routing, there was just a single route, and so single set of route parameters and so all of these
        // may share the same set of combined parameter names.
        // For attribute routing, there may be multiple routes, each with different route parameter names, and 
        // so each instance of a CandidateActionWithParams may have a different parameter set.
        [DebuggerDisplay("{DebuggerToString()}")]
        private class CandidateActionWithParams
        {
            public CandidateActionWithParams(CandidateAction candidateAction, ISet<string> parameters, IHttpRouteData routeDataSource)
            {
                CandidateAction = candidateAction;
                CombinedParameterNames = parameters;
                RouteDataSource = routeDataSource;
            }

            public CandidateAction CandidateAction { get; private set; }
            public ISet<string> CombinedParameterNames { get; private set; }

            // Remember this so that we can apply it for model binding. 
            public IHttpRouteData RouteDataSource { get; private set; }

            public ReflectedHttpActionDescriptor ActionDescriptor
            {
                get
                {
                    return CandidateAction.ActionDescriptor;
                }
            }

            [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Called from DebuggerDisplay")]
            private string DebuggerToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(CandidateAction.DebuggerToString());
                if (CombinedParameterNames.Count > 0)
                {
                    sb.Append(", Params =");
                    foreach (string param in CombinedParameterNames)
                    {
                        sb.AppendFormat(" {0}", param);
                    }
                }
                return sb.ToString();
            }
        }

        // We need to expose ILookup<string, HttpActionDescriptor>, but we have a ILookup<string, ReflectedHttpActionDescriptor>
        // ReflectedHttpActionDescriptor derives from HttpActionDescriptor, but ILookup doesn't support Covariance.
        // Adapter class since ILookup doesn't support Covariance.
        // Fortunately, IGrouping, IEnumerable support Covariance, so it's easy to forward.
        private class LookupAdapter : ILookup<string, HttpActionDescriptor>
        {
            public ILookup<string, ReflectedHttpActionDescriptor> Source;

            public int Count
            {
                get { return Source.Count; }
            }

            public IEnumerable<HttpActionDescriptor> this[string key]
            {
                get { return Source[key]; }
            }

            public bool Contains(string key)
            {
                return Source.Contains(key);
            }

            public IEnumerator<IGrouping<string, HttpActionDescriptor>> GetEnumerator()
            {
                return Source.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return Source.GetEnumerator();
            }
        }
    }
}

namespace ApiActionSelection.System.Web.Http.Routing
{
    // This is static description of an action and can be shared across requests. 
    // Direct routes may cache a list of these. 
    [DebuggerDisplay("{DebuggerToString()}")]
    internal class CandidateAction
    {
        public ReflectedHttpActionDescriptor ActionDescriptor { get; set; }
        public int Order { get; set; }
        public decimal Precedence { get; set; }

        public bool MatchName(string actionName)
        {
            return String.Equals(ActionDescriptor.ActionName, actionName, StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchVerb(HttpMethod method)
        {
            return ActionDescriptor.SupportedHttpMethods.Contains(method);
        }

        internal string DebuggerToString()
        {
            return String.Format(CultureInfo.CurrentCulture, "{0}, Order={1}, Prec={2}", ActionDescriptor.ActionName, Order, Precedence);
        }
    }
}

namespace ApiActionSelection.System.Web.Http.Routing
{
    /// <summary>
    /// Provides keys for looking up route values and data tokens.
    /// </summary>
    internal static class RouteKeys
    {
        // Used to provide the action and controller name in the route values
        public const string ActionKey = "action";
        public const string ControllerKey = "controller";

        // Used to provide the action descriptors to consider in the route data tokens
        public const string ActionsDataTokenKey = "actions";

        // Used to allow customer-provided disambiguation between multiple matching routes
        public const string OrderDataTokenKey = "order";

        // Used to allow URI constraint-based disambiguation between multiple matching routes
        public const string PrecedenceDataTokenKey = "precedence";
    }
}

namespace ApiActionSelection.System.Collections.Generic
{
    /// <summary>
    /// Helper extension methods for fast use of collections.
    /// </summary>
    internal static class CollectionExtensions
    {
        /// <summary>
        /// Return a new array with the value added to the end. Slow and best suited to long lived arrays with few writes relative to reads.
        /// </summary>
        public static T[] AppendAndReallocate<T>(this T[] array, T value)
        {
            Contract.Assert(array != null);

            int originalLength = array.Length;
            T[] newArray = new T[originalLength + 1];
            array.CopyTo(newArray, 0);
            newArray[originalLength] = value;
            return newArray;
        }

        /// <summary>
        /// Return the enumerable as an Array, copying if required. Optimized for common case where it is an Array. 
        /// Avoid mutating the return value.
        /// </summary>
        public static T[] AsArray<T>(this IEnumerable<T> values)
        {
            Contract.Assert(values != null);

            T[] array = values as T[];
            if (array == null)
            {
                array = values.ToArray();
            }
            return array;
        }

        /// <summary>
        /// Return the enumerable as a Collection of T, copying if required. Optimized for the common case where it is 
        /// a Collection of T and avoiding a copy if it implements IList of T. Avoid mutating the return value.
        /// </summary>
        public static Collection<T> AsCollection<T>(this IEnumerable<T> enumerable)
        {
            Contract.Assert(enumerable != null);

            Collection<T> collection = enumerable as Collection<T>;
            if (collection != null)
            {
                return collection;
            }
            // Check for IList so that collection can wrap it instead of copying
            IList<T> list = enumerable as IList<T>;
            if (list == null)
            {
                list = new List<T>(enumerable);
            }
            return new Collection<T>(list);
        }

        /// <summary>
        /// Return the enumerable as a IList of T, copying if required. Avoid mutating the return value.
        /// </summary>
        public static IList<T> AsIList<T>(this IEnumerable<T> enumerable)
        {
            Contract.Assert(enumerable != null);

            IList<T> list = enumerable as IList<T>;
            if (list != null)
            {
                return list;
            }
            return new List<T>(enumerable);
        }

        /// <summary>
        /// Return the enumerable as a List of T, copying if required. Optimized for common case where it is an List of T 
        /// or a ListWrapperCollection of T. Avoid mutating the return value.
        /// </summary>
        public static List<T> AsList<T>(this IEnumerable<T> enumerable)
        {
            Contract.Assert(enumerable != null);

            List<T> list = enumerable as List<T>;
            if (list != null)
            {
                return list;
            }
            ListWrapperCollection<T> listWrapper = enumerable as ListWrapperCollection<T>;
            if (listWrapper != null)
            {
                return listWrapper.ItemsList;
            }
            return new List<T>(enumerable);
        }

        /// <summary>
        /// Remove values from the list starting at the index start.
        /// </summary>
        public static void RemoveFrom<T>(this List<T> list, int start)
        {
            Contract.Assert(list != null);
            Contract.Assert(start >= 0 && start <= list.Count);

            list.RemoveRange(start, list.Count - start);
        }

        /// <summary>
        /// Return the only value from list, the type's default value if empty, or call the errorAction for 2 or more.
        /// </summary>
        public static T SingleDefaultOrError<T, TArg1>(this IList<T> list, Action<TArg1> errorAction, TArg1 errorArg1)
        {
            Contract.Assert(list != null);
            Contract.Assert(errorAction != null);

            switch (list.Count)
            {
                case 0:
                    return default(T);

                case 1:
                    T value = list[0];
                    return value;

                default:
                    errorAction(errorArg1);
                    return default(T);
            }
        }

        /// <summary>
        /// Returns a single value in list matching type TMatch if there is only one, null if there are none of type TMatch or calls the
        /// errorAction with errorArg1 if there is more than one.
        /// </summary>
        public static TMatch SingleOfTypeDefaultOrError<TInput, TMatch, TArg1>(this IList<TInput> list, Action<TArg1> errorAction, TArg1 errorArg1) where TMatch : class
        {
            Contract.Assert(list != null);
            Contract.Assert(errorAction != null);

            TMatch result = null;
            for (int i = 0; i < list.Count; i++)
            {
                TMatch typedValue = list[i] as TMatch;
                if (typedValue != null)
                {
                    if (result == null)
                    {
                        result = typedValue;
                    }
                    else
                    {
                        errorAction(errorArg1);
                        return null;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Convert an ICollection to an array, removing null values. Fast path for case where there are no null values.
        /// </summary>
        public static T[] ToArrayWithoutNulls<T>(this ICollection<T> collection) where T : class
        {
            Contract.Assert(collection != null);

            T[] result = new T[collection.Count];
            int count = 0;
            foreach (T value in collection)
            {
                if (value != null)
                {
                    result[count] = value;
                    count++;
                }
            }
            if (count == collection.Count)
            {
                return result;
            }
            else
            {
                T[] trimmedResult = new T[count];
                Array.Copy(result, trimmedResult, count);
                return trimmedResult;
            }
        }

        /// <summary>
        /// Convert the array to a Dictionary using the keySelector to extract keys from values and the specified comparer. Optimized for array input.
        /// </summary>
        public static Dictionary<TKey, TValue> ToDictionaryFast<TKey, TValue>(this TValue[] array, Func<TValue, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            Contract.Assert(array != null);
            Contract.Assert(keySelector != null);

            Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>(array.Length, comparer);
            for (int i = 0; i < array.Length; i++)
            {
                TValue value = array[i];
                dictionary.Add(keySelector(value), value);
            }
            return dictionary;
        }

        /// <summary>
        /// Convert the list to a Dictionary using the keySelector to extract keys from values and the specified comparer. Optimized for IList of T input with fast path for array.
        /// </summary>
        public static Dictionary<TKey, TValue> ToDictionaryFast<TKey, TValue>(this IList<TValue> list, Func<TValue, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            Contract.Assert(list != null);
            Contract.Assert(keySelector != null);

            TValue[] array = list as TValue[];
            if (array != null)
            {
                return ToDictionaryFast(array, keySelector, comparer);
            }
            return ToDictionaryFastNoCheck(list, keySelector, comparer);
        }

        /// <summary>
        /// Convert the enumerable to a Dictionary using the keySelector to extract keys from values and the specified comparer. Fast paths for array and IList of T.
        /// </summary>
        public static Dictionary<TKey, TValue> ToDictionaryFast<TKey, TValue>(this IEnumerable<TValue> enumerable, Func<TValue, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            Contract.Assert(enumerable != null);
            Contract.Assert(keySelector != null);

            TValue[] array = enumerable as TValue[];
            if (array != null)
            {
                return ToDictionaryFast(array, keySelector, comparer);
            }
            IList<TValue> list = enumerable as IList<TValue>;
            if (list != null)
            {
                return ToDictionaryFastNoCheck(list, keySelector, comparer);
            }
            Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>(comparer);
            foreach (TValue value in enumerable)
            {
                dictionary.Add(keySelector(value), value);
            }
            return dictionary;
        }

        /// <summary>
        /// Convert the list to a Dictionary using the keySelector to extract keys from values and the specified comparer. Optimized for IList of T input. No checking for other types.
        /// </summary>
        private static Dictionary<TKey, TValue> ToDictionaryFastNoCheck<TKey, TValue>(IList<TValue> list, Func<TValue, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            Contract.Assert(list != null);
            Contract.Assert(keySelector != null);

            int listCount = list.Count;
            Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>(listCount, comparer);
            for (int i = 0; i < listCount; i++)
            {
                TValue value = list[i];
                dictionary.Add(keySelector(value), value);
            }
            return dictionary;
        }
    }
}

namespace ApiActionSelection.System.Web.Http.Routing
{
    internal static class HttpRouteExtensions
    {
        // If route is a direct route, get the action descriptors, order and precedence it may map to.
        public static CandidateAction[] GetDirectRouteCandidates(this IHttpRoute route)
        {
            Contract.Assert(route != null);

            IDictionary<string, object> dataTokens = route.DataTokens;
            if (dataTokens == null)
            {
                return null;
            }

            List<CandidateAction> candidates = new List<CandidateAction>();

            ReflectedHttpActionDescriptor[] directRouteActions = null;
            ReflectedHttpActionDescriptor[] possibleDirectRouteActions;
            if (dataTokens.TryGetValue<ReflectedHttpActionDescriptor[]>(RouteKeys.ActionsDataTokenKey, out possibleDirectRouteActions))
            {
                if (possibleDirectRouteActions != null && possibleDirectRouteActions.Length > 0)
                {
                    directRouteActions = possibleDirectRouteActions;
                }
            }

            if (directRouteActions == null)
            {
                return null;
            }

            int order = 0;
            int possibleOrder;
            if (dataTokens.TryGetValue<int>(RouteKeys.OrderDataTokenKey, out possibleOrder))
            {
                order = possibleOrder;
            }

            decimal precedence = 0M;
            decimal possiblePrecedence;

            if (dataTokens.TryGetValue<decimal>(RouteKeys.PrecedenceDataTokenKey, out possiblePrecedence))
            {
                precedence = possiblePrecedence;
            }

            foreach (ReflectedHttpActionDescriptor actionDescriptor in directRouteActions)
            {
                candidates.Add(new CandidateAction
                {
                    ActionDescriptor = actionDescriptor,
                    Order = order,
                    Precedence = precedence
                });
            }

            return candidates.ToArray();
        }
    }
}


namespace ApiActionSelection.System.Collections.Generic
{
    /// <summary>
    /// Extension methods for <see cref="IDictionary{TKey,TValue}"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class DictionaryExtensions
    {
        /// <summary>
        /// Remove entries from dictionary that match the removeCondition.
        /// </summary>
        public static void RemoveFromDictionary<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, Func<KeyValuePair<TKey, TValue>, bool> removeCondition)
        {
            // Pass the delegate as the state to avoid a delegate and closure
            dictionary.RemoveFromDictionary((entry, innerCondition) =>
            {
                return innerCondition(entry);
            },
                removeCondition);
        }

        /// <summary>
        /// Remove entries from dictionary that match the removeCondition.
        /// </summary>
        public static void RemoveFromDictionary<TKey, TValue, TState>(this IDictionary<TKey, TValue> dictionary, Func<KeyValuePair<TKey, TValue>, TState, bool> removeCondition, TState state)
        {
            Contract.Assert(dictionary != null);
            Contract.Assert(removeCondition != null);

            // Because it is not possible to delete while enumerating, a copy of the keys must be taken. Use the size of the dictionary as an upper bound
            // to avoid creating more than one copy of the keys.
            int removeCount = 0;
            TKey[] keys = new TKey[dictionary.Count];
            foreach (var entry in dictionary)
            {
                if (removeCondition(entry, state))
                {
                    keys[removeCount] = entry.Key;
                    removeCount++;
                }
            }
            for (int i = 0; i < removeCount; i++)
            {
                dictionary.Remove(keys[i]);
            }
        }

        /// <summary>
        /// Gets the value of <typeparamref name="T"/> associated with the specified key or <c>default</c> value if
        /// either the key is not present or the value is not of type <typeparamref name="T"/>. 
        /// </summary>
        /// <typeparam name="T">The type of the value associated with the specified key.</typeparam>
        /// <param name="collection">The <see cref="IDictionary{TKey,TValue}"/> instance where <c>TValue</c> is <c>object</c>.</param>
        /// <param name="key">The key whose value to get.</param>
        /// <param name="value">When this method returns, the value associated with the specified key, if the key is found; otherwise, the default value for the type of the value parameter.</param>
        /// <returns><c>true</c> if key was found, value is non-null, and value is of type <typeparamref name="T"/>; otherwise false.</returns>
        public static bool TryGetValue<T>(this IDictionary<string, object> collection, string key, out T value)
        {
            Contract.Assert(collection != null);

            object valueObj;
            if (collection.TryGetValue(key, out valueObj))
            {
                if (valueObj is T)
                {
                    value = (T)valueObj;
                    return true;
                }
            }

            value = default(T);
            return false;
        }

        internal static IEnumerable<KeyValuePair<string, TValue>> FindKeysWithPrefix<TValue>(this IDictionary<string, TValue> dictionary, string prefix)
        {
            Contract.Assert(dictionary != null);
            Contract.Assert(prefix != null);

            TValue exactMatchValue;
            if (dictionary.TryGetValue(prefix, out exactMatchValue))
            {
                yield return new KeyValuePair<string, TValue>(prefix, exactMatchValue);
            }

            foreach (var entry in dictionary)
            {
                string key = entry.Key;

                if (key.Length <= prefix.Length)
                {
                    continue;
                }

                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Everything is prefixed by the empty string
                if (prefix.Length == 0)
                {
                    yield return entry;
                }
                else
                {
                    char charAfterPrefix = key[prefix.Length];
                    switch (charAfterPrefix)
                    {
                        case '[':
                        case '.':
                            yield return entry;
                            break;
                    }
                }
            }
        }
    }
}
namespace ApiActionSelection.System.Collections.ObjectModel
{
    /// <summary>
    /// A class that inherits from Collection of T but also exposes its underlying data as List of T for performance.
    /// </summary>
    internal sealed class ListWrapperCollection<T> : Collection<T>
    {
        private readonly List<T> _items;

        internal ListWrapperCollection()
            : this(new List<T>())
        {
        }

        internal ListWrapperCollection(List<T> list)
            : base(list)
        {
            _items = list;
        }

        internal List<T> ItemsList
        {
            get { return _items; }
        }
    }
}

namespace ApiActionSelection.System.Web.Http.Internal
{
    internal static class HttpParameterBindingExtensions
    {
        public static bool WillReadUri(this HttpParameterBinding parameterBinding)
        {
            if (parameterBinding == null)
            {
                throw new ArgumentNullException("parameterBinding"); // Error.ArgumentNull("parameterBinding");
            }

            IValueProviderParameterBinding valueProviderParameterBinding = parameterBinding as IValueProviderParameterBinding;
            if (valueProviderParameterBinding != null)
            {
                IEnumerable<ValueProviderFactory> valueProviderFactories = valueProviderParameterBinding.ValueProviderFactories;
                if (valueProviderFactories.Any() && valueProviderFactories.All(factory => factory.GetType().GetInterfaces()
                    .Any(x =>x.Name == "IUriValueProviderFactory")))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

namespace ApiActionSelection.System.Web.Http.ValueProviders
{
    internal interface IUriValueProviderFactory
    {
    }
}

namespace ApiActionSelection.System.Web.Http.Internal
{
    /// <summary>
    /// A static class that provides various <see cref="Type"/> related helpers.
    /// </summary>
    internal static class TypeHelper
    {
        private static readonly Type TaskGenericType = typeof(Task<>);

        internal static readonly Type ApiControllerType = typeof(ApiController);

        internal static Type GetTaskInnerTypeOrNull(Type type)
        {
            Contract.Assert(type != null);
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                Type genericTypeDefinition = type.GetGenericTypeDefinition();

                if (TaskGenericType == genericTypeDefinition)
                {
                    return type.GetGenericArguments()[0];
                }
            }

            return null;
        }

        internal static Type[] GetTypeArgumentsIfMatch(Type closedType, Type matchingOpenType)
        {
            if (!closedType.IsGenericType)
            {
                return null;
            }

            Type openType = closedType.GetGenericTypeDefinition();
            return (matchingOpenType == openType) ? closedType.GetGenericArguments() : null;
        }

        internal static bool IsCompatibleObject(Type type, object value)
        {
            return (value == null && TypeAllowsNullValue(type)) || type.IsInstanceOfType(value);
        }

        internal static bool IsNullableValueType(Type type)
        {
            return Nullable.GetUnderlyingType(type) != null;
        }

        internal static bool TypeAllowsNullValue(Type type)
        {
            return !type.IsValueType || IsNullableValueType(type);
        }

        internal static bool IsSimpleType(Type type)
        {
            return type.IsPrimitive ||
                   type.Equals(typeof(string)) ||
                   type.Equals(typeof(DateTime)) ||
                   type.Equals(typeof(Decimal)) ||
                   type.Equals(typeof(Guid)) ||
                   type.Equals(typeof(DateTimeOffset)) ||
                   type.Equals(typeof(TimeSpan));
        }

        internal static bool IsSimpleUnderlyingType(Type type)
        {
            Type underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                type = underlyingType;
            }

            return TypeHelper.IsSimpleType(type);
        }

        internal static bool CanConvertFromString(Type type)
        {
            return TypeHelper.IsSimpleUnderlyingType(type) ||
                TypeHelper.HasStringConverter(type);
        }

        internal static bool HasStringConverter(Type type)
        {
            return TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));
        }

        /// <summary>
        /// Fast implementation to get the subset of a given type.
        /// </summary>
        /// <typeparam name="T">type to search for</typeparam>
        /// <returns>subset of objects that can be assigned to T</returns>
        internal static ReadOnlyCollection<T> OfType<T>(object[] objects) where T : class
        {
            int max = objects.Length;
            List<T> list = new List<T>(max);
            int idx = 0;
            for (int i = 0; i < max; i++)
            {
                T attr = objects[i] as T;
                if (attr != null)
                {
                    list.Add(attr);
                    idx++;
                }
            }
            list.Capacity = idx;

            return new ReadOnlyCollection<T>(list);
        }
    }
}