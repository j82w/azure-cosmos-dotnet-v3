﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Query
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Collections;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Linq;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.Azure.Cosmos.Routing;
    using Newtonsoft.Json;

    internal abstract class DocumentQueryExecutionContextBase : IDocumentQueryExecutionContext
    {
        public struct InitParams
        {
            public IDocumentQueryClient Client { get; }
            public ResourceType ResourceTypeEnum { get; }
            public Type ResourceType { get; }
            public Expression Expression { get; }
            public FeedOptions FeedOptions { get; }
            public string ResourceLink { get; }
            public bool GetLazyFeedResponse { get; }
            public Guid CorrelatedActivityId { get; }

            public InitParams(
                IDocumentQueryClient client,
                ResourceType resourceTypeEnum,
                Type resourceType,
                Expression expression,
                FeedOptions feedOptions,
                string resourceLink,
                bool getLazyFeedResponse,
                Guid correlatedActivityId)
            {
                if(client == null)
                {
                    throw new ArgumentNullException($"{nameof(client)} can not be null.");
                }

                if (resourceType == null)
                {
                    throw new ArgumentNullException($"{nameof(resourceType)} can not be null.");
                }

                if (expression == null)
                {
                    throw new ArgumentNullException($"{nameof(expression)} can not be null.");
                }

                if (feedOptions == null)
                {
                    throw new ArgumentNullException($"{nameof(feedOptions)} can not be null.");
                }

                if (correlatedActivityId == Guid.Empty)
                {
                    throw new ArgumentException($"{nameof(correlatedActivityId)} can not be empty.");
                }

                this.Client = client;
                this.ResourceTypeEnum = resourceTypeEnum;
                this.ResourceType = resourceType;
                this.Expression = expression;
                this.FeedOptions = feedOptions;
                this.ResourceLink = resourceLink;
                this.GetLazyFeedResponse = getLazyFeedResponse;
                this.CorrelatedActivityId = correlatedActivityId;
            }
        }

        public static readonly FeedResponse<dynamic> EmptyFeedResponse = new FeedResponse<dynamic>(
            Enumerable.Empty<dynamic>(),
            Enumerable.Empty<dynamic>().Count(),
            new StringKeyValueCollection());
        protected SqlQuerySpec querySpec;
        private readonly IDocumentQueryClient client;
        private readonly ResourceType resourceTypeEnum;
        private readonly Type resourceType;
        private readonly Expression expression;
        private readonly FeedOptions feedOptions;
        private readonly string resourceLink;
        private readonly bool getLazyFeedResponse;
        private bool isExpressionEvaluated;
        private FeedResponse<dynamic> lastPage;
        private readonly Guid correlatedActivityId;

        protected DocumentQueryExecutionContextBase(
           InitParams initParams)
        {
            this.client = initParams.Client;
            this.resourceTypeEnum = initParams.ResourceTypeEnum;
            this.resourceType = initParams.ResourceType;
            this.expression = initParams.Expression;
            this.feedOptions = initParams.FeedOptions;
            this.resourceLink = initParams.ResourceLink;
            this.getLazyFeedResponse = initParams.GetLazyFeedResponse;
            this.correlatedActivityId = initParams.CorrelatedActivityId;
            this.isExpressionEvaluated = false;
        }

        public bool ShouldExecuteQueryRequest
        {
            get
            {
                return this.QuerySpec != null;
            }
        }

        public IDocumentQueryClient Client
        {
            get
            {
                return this.client;
            }
        }

        public Type ResourceType
        {
            get
            {
                return this.resourceType;
            }
        }

        public ResourceType ResourceTypeEnum
        {
            get
            {
                return this.resourceTypeEnum;
            }
        }

        public string ResourceLink
        {
            get
            {
                return this.resourceLink;
            }
        }

        public int? MaxItemCount
        {
            get
            {
                return this.feedOptions.MaxItemCount;
            }
        }

        protected SqlQuerySpec QuerySpec
        {
            get
            {
                if (!this.isExpressionEvaluated)
                {
                    this.querySpec = DocumentQueryEvaluator.Evaluate(this.expression);
                    this.isExpressionEvaluated = true;
                }

                return this.querySpec;
            }
        }

        protected PartitionKeyInternal PartitionKeyInternal
        {
            get
            {
                return this.feedOptions.PartitionKey == null ? null : this.feedOptions.PartitionKey.InternalKey;
            }
        }

        protected int MaxBufferedItemCount
        {
            get
            {
                return this.feedOptions.MaxBufferedItemCount;
            }
        }

        protected int MaxDegreeOfParallelism
        {
            get
            {
                return this.feedOptions.MaxDegreeOfParallelism;
            }
        }

        protected string PartitionKeyRangeId
        {
            get
            {
                return this.feedOptions.PartitionKeyRangeId;
            }
        }

        protected virtual string ContinuationToken
        {
            get
            {
                return this.lastPage == null ? this.feedOptions.RequestContinuation : this.lastPage.ResponseContinuation;
            }
        }

        public virtual bool IsDone
        {
            get
            {
                return this.lastPage != null && string.IsNullOrEmpty(this.lastPage.ResponseContinuation);
            }
        }

        public Guid CorrelatedActivityId
        {
            get
            {
                return this.correlatedActivityId;
            }
        }

        public async Task<PartitionedQueryExecutionInfo> GetPartitionedQueryExecutionInfoAsync(
            PartitionKeyDefinition partitionKeyDefinition,
            bool requireFormattableOrderByQuery,
            bool isContinuationExpected,
            CancellationToken cancellationToken)
        {
            // $ISSUE-felixfan-2016-07-13: We should probably get PartitionedQueryExecutionInfo from Gateway in GatewayMode

            QueryPartitionProvider queryPartitionProvider = await this.client.GetQueryPartitionProviderAsync(cancellationToken);
            return queryPartitionProvider.GetPartitionedQueryExecutionInfo(this.QuerySpec, partitionKeyDefinition, requireFormattableOrderByQuery, isContinuationExpected);
        }

        public virtual async Task<FeedResponse<dynamic>> ExecuteNextAsync(CancellationToken cancellationToken)
        {
            if (this.IsDone)
            {
                throw new InvalidOperationException(RMResources.DocumentQueryExecutionContextIsDone);
            }

            this.lastPage = await this.ExecuteInternalAsync(cancellationToken);
            return this.lastPage;
        }

        public FeedOptions GetFeedOptions(string continuationToken)
        {
            FeedOptions options = new FeedOptions(this.feedOptions);
            options.RequestContinuation = continuationToken;
            return options;
        }

        public async Task<INameValueCollection> CreateCommonHeadersAsync(FeedOptions feedOptions)
        {
            INameValueCollection requestHeaders = new StringKeyValueCollection();

            ConsistencyLevel defaultConsistencyLevel = await this.client.GetDefaultConsistencyLevelAsync();
            ConsistencyLevel? desiredConsistencyLevel = await this.client.GetDesiredConsistencyLevelAsync();
            if (!string.IsNullOrEmpty(feedOptions.SessionToken) && !ReplicatedResourceClient.IsReadingFromMaster(this.resourceTypeEnum, OperationType.ReadFeed))
            {
                if (defaultConsistencyLevel == ConsistencyLevel.Session || (desiredConsistencyLevel.HasValue && desiredConsistencyLevel.Value == ConsistencyLevel.Session))
                {
                    // Query across partitions is not supported today. Master resources (for e.g., database) 
                    // can span across partitions, whereas server resources (viz: collection, document and attachment)
                    // don't span across partitions. Hence, session token returned by one partition should not be used 
                    // when quering resources from another partition. 
                    // Since master resources can span across partitions, don't send session token to the backend.
                    // As master resources are sync replicated, we should always get consistent query result for master resources,
                    // irrespective of the chosen replica.
                    // For server resources, which don't span partitions, specify the session token 
                    // for correct replica to be chosen for servicing the query result.
                    requestHeaders[HttpConstants.HttpHeaders.SessionToken] = feedOptions.SessionToken;
                }
            }

            requestHeaders[HttpConstants.HttpHeaders.Continuation] = feedOptions.RequestContinuation;
            requestHeaders[HttpConstants.HttpHeaders.IsQuery] = bool.TrueString;

            // Flow the pageSize only when we are not doing client eval
            if (feedOptions.MaxItemCount.HasValue)
            {
                requestHeaders[HttpConstants.HttpHeaders.PageSize] = feedOptions.MaxItemCount.ToString();
            }

            requestHeaders[HttpConstants.HttpHeaders.EnableCrossPartitionQuery] = feedOptions.EnableCrossPartitionQuery.ToString();

            if (feedOptions.MaxDegreeOfParallelism != 0)
            {
                requestHeaders[HttpConstants.HttpHeaders.ParallelizeCrossPartitionQuery] = bool.TrueString;
            }

            if (this.feedOptions.EnableScanInQuery != null)
            {
                requestHeaders[HttpConstants.HttpHeaders.EnableScanInQuery] = this.feedOptions.EnableScanInQuery.ToString();
            }

            if (this.feedOptions.EmitVerboseTracesInQuery != null)
            {
                requestHeaders[HttpConstants.HttpHeaders.EmitVerboseTracesInQuery] = this.feedOptions.EmitVerboseTracesInQuery.ToString();
            }

            if (this.feedOptions.EnableLowPrecisionOrderBy != null)
            {
                requestHeaders[HttpConstants.HttpHeaders.EnableLowPrecisionOrderBy] = this.feedOptions.EnableLowPrecisionOrderBy.ToString();
            }

            if (!string.IsNullOrEmpty(this.feedOptions.FilterBySchemaResourceId))
            {
                requestHeaders[HttpConstants.HttpHeaders.FilterBySchemaResourceId] = this.feedOptions.FilterBySchemaResourceId;
            }

            if (this.feedOptions.ResponseContinuationTokenLimitInKb != null)
            {
                requestHeaders[HttpConstants.HttpHeaders.ResponseContinuationTokenLimitInKB] = this.feedOptions.ResponseContinuationTokenLimitInKb.ToString();
            }

            if (this.feedOptions.ConsistencyLevel.HasValue)
            {
                await this.client.EnsureValidOverwrite(feedOptions.ConsistencyLevel.Value);
                requestHeaders.Set(HttpConstants.HttpHeaders.ConsistencyLevel, this.feedOptions.ConsistencyLevel.Value.ToString());
            }
            else if (desiredConsistencyLevel.HasValue)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.ConsistencyLevel, desiredConsistencyLevel.Value.ToString());
            }

            if (this.feedOptions.EnumerationDirection.HasValue)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.EnumerationDirection, this.feedOptions.EnumerationDirection.Value.ToString());
            }

            if (this.feedOptions.ReadFeedKeyType.HasValue)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.ReadFeedKeyType, this.feedOptions.ReadFeedKeyType.Value.ToString());
            }

            if (this.feedOptions.StartId != null)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.StartId, this.feedOptions.StartId);
            }

            if (this.feedOptions.EndId != null)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.EndId, this.feedOptions.EndId);
            }

            if (this.feedOptions.StartEpk != null)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.StartEpk, this.feedOptions.StartEpk);
            }

            if (this.feedOptions.EndEpk != null)
            {
                requestHeaders.Set(HttpConstants.HttpHeaders.EndEpk, this.feedOptions.EndEpk);
            }

            if (this.feedOptions.PopulateQueryMetrics)
            {
                requestHeaders[HttpConstants.HttpHeaders.PopulateQueryMetrics] = bool.TrueString;
            }

            if (this.feedOptions.ForceQueryScan)
            {
                requestHeaders[HttpConstants.HttpHeaders.ForceQueryScan] = bool.TrueString;
            }

            if (this.feedOptions.ContentSerializationFormat.HasValue)
            {
                requestHeaders[HttpConstants.HttpHeaders.ContentSerializationFormat] = this.feedOptions.ContentSerializationFormat.Value.ToString();
            }

            return requestHeaders;
        }

        public DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec, PartitionKeyInternal partitionKey)
        {
            DocumentServiceRequest request = this.CreateDocumentServiceRequest(requestHeaders, querySpec);
            this.PopulatePartitionKeyInfo(request, partitionKey);

            return request;
        }

        public DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec, PartitionKeyRange targetRange, string collectionRid)
        {
            DocumentServiceRequest request = this.CreateDocumentServiceRequest(requestHeaders, querySpec);

            this.PopulatePartitionKeyRangeInfo(request, targetRange, collectionRid);

            return request;
        }

        public async Task<FeedResponse<dynamic>> ExecuteRequestAsync(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return await (this.ShouldExecuteQueryRequest ? 
                this.ExecuteQueryRequestAsync(request, cancellationToken) : 
                this.ExecuteReadFeedRequestAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteRequestAsync<T>(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return await (this.ShouldExecuteQueryRequest ? 
                this.ExecuteQueryRequestAsync<T>(request, cancellationToken) : 
                this.ExecuteReadFeedRequestAsync<T>(request, cancellationToken));
        }

        public async Task<FeedResponse<dynamic>> ExecuteQueryRequestAsync(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return this.GetFeedResponse(await this.ExecuteQueryRequestInternalAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteQueryRequestAsync<T>(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return this.GetFeedResponse<T>(await this.ExecuteQueryRequestInternalAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<dynamic>> ExecuteReadFeedRequestAsync(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return this.GetFeedResponse(await this.client.ReadFeedAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteReadFeedRequestAsync<T>(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            return this.GetFeedResponse<T>(await this.client.ReadFeedAsync(request, cancellationToken));
        }

        public void PopulatePartitionKeyRangeInfo(DocumentServiceRequest request, PartitionKeyRange range, string collectionRid)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (range == null)
            {
                throw new ArgumentNullException("range");
            }

            if (this.resourceTypeEnum.IsPartitioned())
            {
                request.RouteTo(new PartitionKeyRangeIdentity(collectionRid, range.Id));
            }
        }

        public async Task<PartitionKeyRange> GetTargetPartitionKeyRangeById(string collectionResourceId, string partitionKeyRangeId)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();

            PartitionKeyRange range = await routingMapProvider.TryGetPartitionKeyRangeByIdAsync(collectionResourceId, partitionKeyRangeId);
            if (range == null && PathsHelper.IsNameBased(this.resourceLink))
            {
                // Refresh the cache and don't try to reresolve collection as it is not clear what already
                // happened based on previously resolved collection rid.
                // Return NotFoundException this time. Next query will succeed.
                // This can only happen if collection is deleted/created with same name and client was not restarted
                // inbetween.
                CollectionCache collectionCache = await this.Client.GetCollectionCacheAsync();
                collectionCache.Refresh(this.resourceLink);
            }

            if (range == null)
            {
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRangeById(collectionResourceId:{collectionResourceId}, partitionKeyRangeId: {partitionKeyRangeId}) failed due to stale cache");
            }

            return range;
        }

        public async Task<List<PartitionKeyRange>> GetTargetPartitionKeyRanges(string collectionResourceId, List<Range<string>> providedRanges)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();

            List<PartitionKeyRange> ranges = await routingMapProvider.TryGetOverlappingRangesAsync(collectionResourceId, providedRanges);
            if (ranges == null && PathsHelper.IsNameBased(this.resourceLink))
            {
                // Refresh the cache and don't try to reresolve collection as it is not clear what already
                // happened based on previously resolved collection rid.
                // Return NotFoundException this time. Next query will succeed.
                // This can only happen if collection is deleted/created with same name and client was not restarted
                // inbetween.
                CollectionCache collectionCache = await this.Client.GetCollectionCacheAsync();
                collectionCache.Refresh(this.resourceLink);
            }

            if (ranges == null)
            {
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRanges(collectionResourceId:{collectionResourceId}, providedRanges: {string.Join(",", providedRanges)} failed due to stale cache");
            }

            return ranges;
        }

        public abstract void Dispose();

        protected abstract Task<FeedResponse<dynamic>> ExecuteInternalAsync(CancellationToken cancellationToken);

        protected async Task<List<PartitionKeyRange>> GetReplacementRanges(PartitionKeyRange targetRange, string collectionRid)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();
            List<PartitionKeyRange> replacementRanges = (await routingMapProvider.TryGetOverlappingRangesAsync(collectionRid, targetRange.ToRange(), true)).ToList();
            string replaceMinInclusive = replacementRanges.First().MinInclusive;
            string replaceMaxExclusive = replacementRanges.Last().MaxExclusive;
            if (!replaceMinInclusive.Equals(targetRange.MinInclusive, StringComparison.Ordinal) || !replaceMaxExclusive.Equals(targetRange.MaxExclusive, StringComparison.Ordinal))
            {
                throw new InternalServerErrorException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Target range and Replacement range has mismatched min/max. Target range: [{0}, {1}). Replacement range: [{2}, {3}).",
                    targetRange.MinInclusive,
                    targetRange.MaxExclusive,
                    replaceMinInclusive,
                    replaceMaxExclusive));
            }

            return replacementRanges;
        }

        protected bool NeedPartitionKeyRangeCacheRefresh(DocumentClientException ex)
        {
            return ex.StatusCode == (HttpStatusCode)StatusCodes.Gone && ex.GetSubStatus() == SubStatusCodes.PartitionKeyRangeGone;
        }

        private async Task<DocumentServiceResponse> ExecuteQueryRequestInternalAsync(
            DocumentServiceRequest request, 
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client.ExecuteQueryAsync(request, cancellationToken);
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        private DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec)
        {
            DocumentServiceRequest request = querySpec != null ?
                this.CreateQueryDocumentServiceRequest(requestHeaders, querySpec) :
                this.CreateReadFeedDocumentServiceRequest(requestHeaders);

            if (this.feedOptions.JsonSerializerSettings != null)
            {
                request.SerializerSettings = this.feedOptions.JsonSerializerSettings;
            }

            return request;
        }

        private DocumentServiceRequest CreateQueryDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec)
        {
            DocumentServiceRequest executeQueryRequest;

            string queryText;
            switch (this.client.QueryCompatibilityMode)
            {
                case QueryCompatibilityMode.SqlQuery:
                    if (querySpec.Parameters != null && querySpec.Parameters.Count > 0)
                    {
                        throw new ArgumentException(
                            string.Format(CultureInfo.InvariantCulture, "Unsupported argument in query compatibility mode '{0}'", this.client.QueryCompatibilityMode),
                            "querySpec.Parameters");
                    }

                    executeQueryRequest = DocumentServiceRequest.Create(
                        OperationType.SqlQuery,
                        this.resourceTypeEnum,
                        this.resourceLink,
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);

                    executeQueryRequest.Headers[HttpConstants.HttpHeaders.ContentType] = RuntimeConstants.MediaTypes.SQL;
                    queryText = querySpec.QueryText;
                    break;

                case QueryCompatibilityMode.Default:
                case QueryCompatibilityMode.Query:
                default:
                    executeQueryRequest = DocumentServiceRequest.Create(
                        OperationType.Query,
                        this.resourceTypeEnum,
                        this.resourceLink,
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);

                    executeQueryRequest.Headers[HttpConstants.HttpHeaders.ContentType] = RuntimeConstants.MediaTypes.QueryJson;
                    queryText = JsonConvert.SerializeObject(querySpec);
                    break;
            }

            executeQueryRequest.Body = new MemoryStream(Encoding.UTF8.GetBytes(queryText));
            return executeQueryRequest;
        }

        private DocumentServiceRequest CreateReadFeedDocumentServiceRequest(INameValueCollection requestHeaders)
        {
            if (this.resourceTypeEnum == Microsoft.Azure.Cosmos.Internal.ResourceType.Database
                || this.resourceTypeEnum == Microsoft.Azure.Cosmos.Internal.ResourceType.Offer)
            {
                return DocumentServiceRequest.Create(
                    OperationType.ReadFeed,
                    null,
                    this.resourceTypeEnum,
                    AuthorizationTokenType.PrimaryMasterKey,
                    requestHeaders);
            }
            else
            {
                return DocumentServiceRequest.Create(
                   OperationType.ReadFeed,
                   this.resourceTypeEnum,
                   this.resourceLink,
                   AuthorizationTokenType.PrimaryMasterKey,
                   requestHeaders);
            }
        }

        private void PopulatePartitionKeyInfo(DocumentServiceRequest request, PartitionKeyInternal partitionKey)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (this.resourceTypeEnum.IsPartitioned())
            {
                if (partitionKey != null)
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = partitionKey.ToJsonString();
                }
            }
        }

        private FeedResponse<dynamic> GetFeedResponse(DocumentServiceResponse response)
        {
            int itemCount = 0;

            long responseLengthBytes = response.ResponseBody.CanSeek ? response.ResponseBody.Length : 0;

            IEnumerable<dynamic> responseFeed = response.GetQueryResponse(this.resourceType, out itemCount);

            return new FeedResponse<dynamic>(responseFeed, itemCount, response.Headers, response.RequestStats, responseLengthBytes);
        }

        private FeedResponse<T> GetFeedResponse<T>(DocumentServiceResponse response)
        {
            int itemCount = 0;

            long responseLengthBytes = response.ResponseBody.CanSeek ? response.ResponseBody.Length : 0;

            IEnumerable<T> responseFeed = response.GetQueryResponse<T>(this.resourceType, this.getLazyFeedResponse, out itemCount);

            return new FeedResponse<T>(responseFeed, itemCount, response.Headers, response.RequestStats, responseLengthBytes);
        }
    }
}