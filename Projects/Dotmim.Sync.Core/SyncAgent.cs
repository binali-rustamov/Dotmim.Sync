﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Filter;
using Dotmim.Sync.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{

    /// <summary>
    /// Sync agent. It's the sync orchestrator
    /// Knows both the Sync Server provider and the Sync Client provider
    /// </summary>
    public class SyncAgent : IDisposable
    {
        /// <summary>
        /// Defines the state that a synchronization session is in.
        /// </summary>
        public SyncSessionState SessionState { get; set; }

        /// <summary>
        /// Gets or Sets the provider for the Client Side
        /// </summary>
        public CoreProvider LocalProvider { get; set; }

        /// <summary>
        /// Get or Sets the provider for the Server Side
        /// </summary>
        public IProvider RemoteProvider { get; set; }

        // Scope informaitons. 
        // On Server, we have tow scopes available : Server Scope and Client (server timestamp) scope
        // On Client, we have only the client scope
        public Dictionary<string, ScopeInfo> Scopes { get; set; }

        /// <summary>
        /// Get or Sets the Sync parameter to pass to Remote provider for filtering rows
        /// </summary>
        public SyncParameterCollection Parameters { get; private set; }

        /// <summary>
        /// Occurs when sync is starting, ending
        /// </summary>
        public event EventHandler<SyncSessionState> SessionStateChanged = null;

        /// <summary>
        /// Set Sync Configuration parameters
        /// </summary>
        public void SetConfiguration(Action<SyncConfiguration> configuration)
            => this.LocalProvider.SetConfiguration(configuration);

        /// <summary>
        /// Set Sync Options parameters
        /// </summary>
        public void SetOptions(Action<SyncOptions> options)
            => this.LocalProvider.SetOptions(options);


        /// <summary>
        /// set the progress action used to get progression on the provider
        /// </summary>
        public void SetProgress(IProgress<ProgressArgs> progress)
            => this.LocalProvider.SetProgress(progress);

        /// <summary>
        /// Set an interceptor to get info on the current sync process
        /// </summary>
        public void SetInterceptor(InterceptorBase interceptor)
            => this.LocalProvider.SetInterceptor(interceptor);


        /// <summary>
        /// SyncAgent manage both server and client provider
        /// It's the main object to launch the Sync process
        /// </summary>
        public SyncAgent(string scopeName, CoreProvider localProvider, IProvider remoteProvider)
        {
            if (string.IsNullOrEmpty(scopeName))
                throw new ArgumentNullException("scopeName");
            this.LocalProvider = localProvider ?? throw new ArgumentNullException("ClientProvider");
            this.RemoteProvider = remoteProvider ?? throw new ArgumentNullException("ServerProvider");

            this.LocalProvider.SetConfiguration(c => c.ScopeName = scopeName);
            this.RemoteProvider.SetConfiguration(c => c.ScopeName = scopeName);

            this.Parameters = new SyncParameterCollection();
        }


        /// <summary>
        /// SyncAgent used in a web proxy sync session. No need to set tables, it's done from the server web api side.
        /// </summary>
        public SyncAgent(CoreProvider localProvider, IProvider remoteProvider)
            : this("DefaultScope", localProvider, remoteProvider)
        {
        }

        /// <summary>
        /// SyncAgent manage both server and client provider
        /// the tables array represents the tables you want to sync
        /// Don't work on the proxy provider
        /// </summary>
        public SyncAgent(string scopeName, CoreProvider clientProvider, IProvider serverProvider, string[] tables)
        : this(scopeName, clientProvider, serverProvider)
        {
            if (tables == null || tables.Length <= 0)
                throw new ArgumentException("you need to pass at lease one table name");

            if (!(this.RemoteProvider is CoreProvider remoteCoreProvider))
                throw new ArgumentException("Since the remote provider is a web proxy, you have to configure the server side");

            if (!remoteCoreProvider.CanBeServerProvider)
                throw new NotSupportedException();

            this.LocalProvider.SetConfiguration(c =>
            {
                foreach (var tbl in tables)
                    c.Add(tbl);
            });
            this.RemoteProvider.SetConfiguration(c =>
            {
                foreach (var tbl in tables)
                    c.Add(tbl);
            });
        }

        /// <summary>
        /// SyncAgent manage both server and client provider
        /// the tables array represents the tables you want to sync
        /// Don't work on the proxy provider
        /// </summary>
        public SyncAgent(CoreProvider clientProvider, IProvider serverProvider, string[] tables)
        : this("DefaultScope", clientProvider, serverProvider, tables)
        {
        }

        /// <summary>
        /// Launch a normal synchronization
        /// </summary>
        public async Task<SyncContext> SynchronizeAsync() => await this.SynchronizeAsync(SyncType.Normal, CancellationToken.None);

        /// <summary>
        /// Launch a normal synchronization with a cancellation token
        /// </summary>
        public async Task<SyncContext> SynchronizeAsync(CancellationToken cancellationToken) => await this.SynchronizeAsync(SyncType.Normal, cancellationToken);

        /// <summary>
        /// Launch a normal synchronization with a progress object
        /// </summary>
        public async Task<SyncContext> SynchronizeAsync(IProgress<ProgressArgs> progress) => await this.SynchronizeAsync(SyncType.Normal, CancellationToken.None, progress);

        /// <summary>
        /// Launch a synchronization with the specified mode
        /// </summary>
        public async Task<SyncContext> SynchronizeAsync(SyncType syncType) => await this.SynchronizeAsync(syncType, CancellationToken.None);

        /// <summary>
        /// Launch a synchronization with the specified mode
        /// </summary>
        public async Task<SyncContext> SynchronizeAsync(SyncType syncType, CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {
            // Context, used to back and forth data between servers
            var context = new SyncContext(Guid.NewGuid())
            {
                // set start time
                StartTime = DateTime.Now,
                // if any parameters, set in context
                Parameters = this.Parameters,
                // set sync type (Normal, Reinitialize, ReinitializeWithUpload)
                SyncType = syncType
            };

            this.SessionState = SyncSessionState.Synchronizing;
            this.SessionStateChanged?.Invoke(this, this.SessionState);

            ScopeInfo localScopeInfo = null,
                      serverScopeInfo = null,
                      localScopeReferenceInfo = null,
                      scope = null;

            var fromId = Guid.Empty;
            var lastSyncTS = 0L;
            var isNew = true;

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Setting the cancellation token
                this.LocalProvider.SetCancellationToken(cancellationToken);
                this.RemoteProvider.SetCancellationToken(cancellationToken);

                // Setting progress
                this.LocalProvider.SetProgress(progress);

                // ----------------------------------------
                // 0) Begin Session / Get the Configuration from remote provider
                //    If the configuration object is provided by the client, the server will be updated with it.
                // ----------------------------------------
                (context, this.LocalProvider.Configuration) = await this.RemoteProvider.BeginSessionAsync(context,
                    new MessageBeginSession { Configuration = this.LocalProvider.Configuration });


                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Locally, nothing really special. Eventually, editing the config object
                (context, this.LocalProvider.Configuration) = await this.LocalProvider.BeginSessionAsync(context,
                    new MessageBeginSession { Configuration = this.LocalProvider.Configuration });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // ----------------------------------------
                // 1) Read scope info
                // ----------------------------------------

                // get the scope from local provider 
                List<ScopeInfo> localScopes;
                List<ScopeInfo> serverScopes;
                (context, localScopes) = await this.LocalProvider.EnsureScopesAsync(context,
                    new MessageEnsureScopes
                    {
                        ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                        ScopeName = this.LocalProvider.Configuration.ScopeName,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (localScopes.Count != 1)
                    throw new Exception("On Local provider, we should have only one scope info");

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                localScopeInfo = localScopes[0];

                (context, serverScopes) = await this.RemoteProvider.EnsureScopesAsync(context,
                    new MessageEnsureScopes
                    {
                        ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                        ScopeName = this.LocalProvider.Configuration.ScopeName,
                        ClientReferenceId = localScopeInfo.Id,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (serverScopes.Count != 2)
                    throw new Exception("On Remote provider, we should have two scopes (one for server and one for client side)");

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                serverScopeInfo = serverScopes.First(s => s.Id != localScopeInfo.Id);
                localScopeReferenceInfo = serverScopes.First(s => s.Id == localScopeInfo.Id);

                // ----------------------------------------
                // 2) Build Configuration Object
                // ----------------------------------------

                // Get Schema from remote provider
                (context, this.LocalProvider.Configuration.Schema) = await this.RemoteProvider.EnsureSchemaAsync(context,
                    new MessageEnsureSchema
                    {
                        Schema = this.LocalProvider.Configuration.Schema,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Apply on local Provider
                (context, this.LocalProvider.Configuration.Schema) = await this.LocalProvider.EnsureSchemaAsync(context,
                    new MessageEnsureSchema
                    {
                        Schema = this.LocalProvider.Configuration.Schema,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // ----------------------------------------
                // 3) Ensure databases are ready
                // ----------------------------------------

                // Server should have already the schema
                context = await this.RemoteProvider.EnsureDatabaseAsync(context,
                    new MessageEnsureDatabase
                    {
                        ScopeInfo = serverScopeInfo,
                        Schema = this.LocalProvider.Configuration.Schema,
                        Filters = this.LocalProvider.Configuration.Filters,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Client could have, or not, the tables
                context = await this.LocalProvider.EnsureDatabaseAsync(context,
                    new MessageEnsureDatabase
                    {
                        ScopeInfo = localScopeInfo,
                        Schema = this.LocalProvider.Configuration.Schema,
                        Filters = this.LocalProvider.Configuration.Filters,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // ----------------------------------------
                // 5) Get changes and apply them
                // ----------------------------------------
                BatchInfo clientBatchInfo;
                BatchInfo serverBatchInfo;

                DatabaseChangesSelected clientChangesSelected = null;
                DatabaseChangesSelected serverChangesSelected = null;
                DatabaseChangesApplied clientChangesApplied = null;
                DatabaseChangesApplied serverChangesApplied = null;

                // those timestamps will be registered as the "timestamp just before launch the sync"
                long serverTimestamp, clientTimestamp;

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Apply on the Server Side
                // Since we are on the server, 
                // we need to check the server client timestamp (not the client timestamp which is completely different)
                var serverPolicy = this.LocalProvider.Configuration.ConflictResolutionPolicy;
                var clientPolicy = serverPolicy == ConflictResolutionPolicy.ServerWins ? ConflictResolutionPolicy.ClientWins : ConflictResolutionPolicy.ServerWins;

                // We get from local provider all rows not last updated from the server
                fromId = serverScopeInfo.Id;
                // lastSyncTS : get lines inserted / updated / deteleted after the last sync commited
                lastSyncTS = localScopeInfo.LastSyncTimestamp;
                // isNew : If isNew, lasttimestamp is not correct, so grab all
                isNew = localScopeInfo.IsNewScope;
                //Direction set to Upload
                context.SyncWay = SyncWay.Upload;

                // JUST before the whole process, get the timestamp, to be sure to 
                // get rows inserted / updated elsewhere since the sync is not over
                (context, clientTimestamp) = await this.LocalProvider.GetLocalTimestampAsync(context,
                    new MessageTimestamp
                    {
                        ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, Timestamp = lastSyncTS };
                (context, clientBatchInfo, clientChangesSelected) =
                    await this.LocalProvider.GetChangeBatchAsync(context,
                        new MessageGetChangesBatch
                        {
                            ScopeInfo = scope,
                            Schema = this.LocalProvider.Configuration.Schema,
                            //BatchSize = this.Options.BatchSize,
                            //BatchDirectory = this.Options.BatchDirectory,
                            Policy = clientPolicy,
                            Filters = this.LocalProvider.Configuration.Filters,
                            SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                        });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();



                // fromId : When applying rows, make sure it's identified as applied by this client scope
                fromId = localScopeInfo.Id;
                // lastSyncTS : apply lines only if thye are not modified since last client sync
                lastSyncTS = localScopeReferenceInfo.LastSyncTimestamp;
                // isNew : not needed
                isNew = false;
                scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, Timestamp = lastSyncTS };

                (context, serverChangesApplied) =
                    await this.RemoteProvider.ApplyChangesAsync(context,
                     new MessageApplyChanges
                     {
                         FromScope = scope,
                         Schema = this.LocalProvider.Configuration.Schema,
                         Policy = serverPolicy,
                         ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                         Changes = clientBatchInfo,
                         SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                     });


                // if ConflictResolutionPolicy.ClientWins or Handler set to Client wins
                // Conflict occurs here and server loose. 
                // Conflicts count should be temp saved because applychanges on client side won't raise any conflicts (and so property Context.TotalSyncConflicts will be reset to 0)
                var conflictsOnRemoteCount = context.TotalSyncConflicts;

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                // Get changes from server


                // get the archive if exists
                //if (localScopeReferenceInfo.IsNewScope && !string.IsNullOrEmpty(this.Configuration.Archive))
                //{
                //// fromId : Make sure we don't select lines on server that has been already updated by the client
                //fromId = localScopeInfo.Id;
                //// lastSyncTS : apply lines only if thye are not modified since last client sync
                //lastSyncTS = localScopeReferenceInfo.LastTimestamp;
                //// isNew : make sure we take all lines if it's the first time we get 
                //isNew = localScopeReferenceInfo.IsNewScope;
                //scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, LastTimestamp = lastSyncTS };
                ////Direction set to Download
                //context.SyncWay = SyncWay.Download;

                //(context, serverBatchInfo, serverChangesSelected) = await this.RemoteProvider.GetArchiveAsync(context, scope);

                //// fromId : When applying rows, make sure it's identified as applied by this server scope
                //fromId = serverScopeInfo.Id;
                //// lastSyncTS : apply lines only if they are not modified since last client sync
                //lastSyncTS = localScopeInfo.LastTimestamp;
                //// isNew : if IsNew, don't apply deleted rows from server
                //isNew = localScopeInfo.IsNewScope;
                //scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, LastTimestamp = lastSyncTS };

                //(context, clientChangesApplied) = await this.LocalProvider.ApplyArchiveAsync(context, scope, serverBatchInfo);

                //// Here we have to change the localScopeInfo.LastTimestamp to the good one
                //// last ts from archive
                //localScopeReferenceInfo.LastTimestamp = [something from the archive];
                //// we are not new anymore 
                //localScopeReferenceInfo.IsNewScope = false;
                //}


                // fromId : Make sure we don't select lines on server that has been already updated by the client
                fromId = localScopeInfo.Id;
                // lastSyncTS : apply lines only if thye are not modified since last client sync
                lastSyncTS = localScopeReferenceInfo.LastSyncTimestamp;
                // isNew : make sure we take all lines if it's the first time we get 
                isNew = localScopeReferenceInfo.IsNewScope;
                scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, Timestamp = lastSyncTS };
                //Direction set to Download
                context.SyncWay = SyncWay.Download;

                // JUST Before get changes, get the timestamp, to be sure to 
                // get rows inserted / updated elsewhere since the sync is not over
                (context, serverTimestamp) = await this.RemoteProvider.GetLocalTimestampAsync(context,
                    new MessageTimestamp
                    {
                        ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                        SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                    });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                (context, serverBatchInfo, serverChangesSelected) =
                    await this.RemoteProvider.GetChangeBatchAsync(context,
                        new MessageGetChangesBatch
                        {
                            ScopeInfo = scope,
                            Schema = this.LocalProvider.Configuration.Schema,
                            Policy = serverPolicy,
                            Filters = this.LocalProvider.Configuration.Filters,
                            SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                        });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();



                // Apply local changes

                // fromId : When applying rows, make sure it's identified as applied by this server scope
                fromId = serverScopeInfo.Id;
                // lastSyncTS : apply lines only if they are not modified since last client sync
                lastSyncTS = localScopeInfo.LastSyncTimestamp;
                // isNew : if IsNew, don't apply deleted rows from server
                isNew = localScopeInfo.IsNewScope;
                scope = new ScopeInfo { Id = fromId, IsNewScope = isNew, Timestamp = lastSyncTS };

                (context, clientChangesApplied) =
                    await this.LocalProvider.ApplyChangesAsync(context,
                        new MessageApplyChanges
                        {
                            FromScope = scope,
                            Schema = this.LocalProvider.Configuration.Schema,
                            Policy = clientPolicy,
                            ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                            Changes = serverBatchInfo,
                            SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                        });


                context.TotalChangesDownloaded = clientChangesApplied.TotalAppliedChanges;
                context.TotalChangesUploaded = clientChangesSelected.TotalChangesSelected;
                context.TotalSyncErrors = clientChangesApplied.TotalAppliedChangesFailed;

                context.CompleteTime = DateTime.Now;

                serverScopeInfo.IsNewScope = false;
                localScopeReferenceInfo.IsNewScope = false;
                localScopeInfo.IsNewScope = false;

                serverScopeInfo.LastSync = context.CompleteTime;
                localScopeReferenceInfo.LastSync = context.CompleteTime;
                localScopeInfo.LastSync = context.CompleteTime;

                serverScopeInfo.LastSyncTimestamp = serverTimestamp;
                localScopeReferenceInfo.LastSyncTimestamp = serverTimestamp;
                localScopeInfo.LastSyncTimestamp = clientTimestamp;

                var duration = context.CompleteTime.Subtract(context.StartTime);
                serverScopeInfo.LastSyncDuration = duration.Ticks;
                localScopeReferenceInfo.LastSyncDuration = duration.Ticks;
                localScopeInfo.LastSyncDuration = duration.Ticks;

                serverScopeInfo.IsLocal = true;
                localScopeReferenceInfo.IsLocal = false;

                context = await this.RemoteProvider.WriteScopesAsync(context,
                        new MessageWriteScopes
                        {
                            ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                            Scopes = new List<ScopeInfo> { serverScopeInfo, localScopeReferenceInfo },
                            SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                        });


                serverScopeInfo.IsLocal = false;
                localScopeInfo.IsLocal = true;

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                context = await this.LocalProvider.WriteScopesAsync(context,
                        new MessageWriteScopes
                        {
                            ScopeInfoTableName = this.LocalProvider.Configuration.ScopeInfoTableName,
                            Scopes = new List<ScopeInfo> { localScopeInfo, serverScopeInfo },
                            SerializationFormat = this.LocalProvider.Configuration.SerializationFormat
                        });

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

            }
            catch (SyncException se)
            {
                Console.WriteLine($"Sync Exception: {se.Message}. Type:{se.Type}.");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unknwon Exception: {ex.Message}.");
                throw new SyncException(ex, SyncStage.None);
            }
            finally
            {
                // End the current session
                context = await this.RemoteProvider.EndSessionAsync(context);
                context = await this.LocalProvider.EndSessionAsync(context);

                this.SessionState = SyncSessionState.Ready;
                this.SessionStateChanged?.Invoke(this, this.SessionState);
            }

            return context;
        }


        // --------------------------------------------------------------------
        // Dispose
        // --------------------------------------------------------------------

        /// <summary>
        /// Releases all resources used by the <see cref="T:Microsoft.Synchronization.Data.DbSyncBatchInfo" />.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used 
        /// by the <see cref="T:Microsoft.Synchronization.Data.DbSyncBatchInfo" /> and optionally releases the managed resources.
        /// </summary>
        protected virtual void Dispose(bool cleanup)
        {

        }
    }
}
