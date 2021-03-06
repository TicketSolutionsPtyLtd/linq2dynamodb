﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2.DocumentModel;
using Linq2DynamoDb.DataContext.Caching;
using Linq2DynamoDb.DataContext.ExpressionUtils;
using Linq2DynamoDb.DataContext.Utils;

namespace Linq2DynamoDb.DataContext
{
    /// <summary>
    /// Implements entity creating, updating and deleting
    /// </summary>
    public class TableDefinitionWrapper : TableDefinitionWrapperBase
    {
        #region ctor

        internal TableDefinitionWrapper(Table tableDefinition, Type tableEntityType, object hashKeyValue, ITableCache cacheImplementation, bool consistentRead) 
            : 
            base(tableDefinition, tableEntityType, hashKeyValue, cacheImplementation, consistentRead)
        {
            if (this.HashKeyValue == null)
            {
                this.ToDocumentConversionFunctor = DynamoDbConversionUtils.ToDocumentConverter(this.TableEntityType);
            }
            else
            {
                var converter = DynamoDbConversionUtils.ToDocumentConverter(this.TableEntityType);
                // adding a step for filling in the predefined HashKey value
                this.ToDocumentConversionFunctor = entity =>
                    {
                        var doc = converter(entity);
                        doc[this.TableDefinition.HashKeys[0]] = this.HashKeyValue;
                        return doc;
                    };
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Registers an added entity
        /// </summary>
        public void AddNewEntity(object newEntity)
        {
            this._addedEntities.Add(new EntityWrapper(newEntity, this.ToDocumentConversionFunctor, this.EntityKeyGetter));
        }

        /// <summary>
        /// Registers a removed entity
        /// </summary>
        public void RemoveEntity(object removedEntity)
        {
            EntityKey removedKey = null;
            if (removedEntity != null)
            {
                try
                {
                    removedKey = this.EntityKeyGetter.GetKey(removedEntity);
                }
                catch (Exception)
                {
                    removedKey = null;
                }
            }

            if (removedKey == null)
            {
                // just skipping entities with no key specified
                return;
            }

            // the process of filling an index in cache should be cancelled
            this.CurrentIndexCreator = null;

            // checking if this is a newly added entity
            int i = 0;
            while (i < this._addedEntities.Count)
            {
                var addedEntityWrapper = this._addedEntities[i];
                if (ReferenceEquals(addedEntityWrapper.Entity, removedEntity))
                {
                    this._addedEntities.RemoveAt(i);

                    // if this entity was not even sent to the server
                    if (!addedEntityWrapper.IsCommited)
                    {
                        // then there's no need to remove it
                        return;
                    }
                }
                else
                {
                    i++;
                }
            }

            this._removedEntities.Add(removedKey);

            // removing the entity from the list of loaded entities as well
            this._loadedEntities.Remove(removedKey);
        }

        /// <summary>
        /// Called by base class, before a new get/query/scan request is sent to DynamoDb
        /// </summary>
        public override void ClearModifications()
        {
            // Skipping added and removed entities.
            // Loaded (and probably then modified) entities should not be skipped.
            this._addedEntities.Clear();
            this._removedEntities.Clear();
        }

        #endregion

        #region Private Properties

        /// <summary>
        /// An action, that must be executed after next submit
        /// </summary>
        internal Action<Exception> ThingsToDoUponSubmit;

        /// <summary>
        /// Entities, loaded from DynamoDb
        /// </summary>
        private readonly Dictionary<EntityKey, IEntityWrapper> _loadedEntities = new Dictionary<EntityKey, IEntityWrapper>();

        /// <summary>
        /// Entities, that were added locally
        /// </summary>
        private readonly List<EntityWrapper> _addedEntities = new List<EntityWrapper>();

        /// <summary>
        /// Keys of removed entities
        /// </summary>
        private readonly HashSet<EntityKey> _removedEntities = new HashSet<EntityKey>();

        /// <summary>
        /// Implements converting objects into documents (caches everything it needs for that)
        /// </summary>
        internal readonly Func<object, Document> ToDocumentConversionFunctor;  

        #endregion

        #region Private Methods

        /// <summary>
        /// Sends all modifications to DynamoDb
        /// </summary>
        protected internal Task SubmitChangesAsync()
        {
            var entitiesToAdd = new Dictionary<EntityKey, Document>();
            var entitiesToUpdate = new Dictionary<EntityKey, Document>();
            var entitiesToRemove = new List<EntityKey>();

            // all newly added (even already saved and not modified any more) entities
            var addedEntitiesDictionary = new Dictionary<EntityKey, IEntityWrapper>();

            try
            {
                // saving modified entities
                foreach (var wrapper in this._loadedEntities.Values)
                {
                    var modifiedDoc = wrapper.GetDocumentIfDirty();
                    if (modifiedDoc == null)
                    {
                        continue;
                    }

                    var modifiedKey = this.EntityKeyGetter.GetKey(modifiedDoc);

                    // no need to modify the entity, if it was removed
                    if (this._removedEntities.Contains(modifiedKey))
                    {
                        continue;
                    }

                    this.Log("Putting modified entity with key {0}", modifiedKey);
                    entitiesToUpdate.Add(modifiedKey, modifiedDoc);
                }

                foreach (var addedEntity in this._addedEntities)
                {
                    var addedKey = addedEntity.EntityKey;
                    // there should be no way to add an existing entity
                    //TODO: add support for entities, that were removed and then added anew
                    if
                    (
                        (this._loadedEntities.ContainsKey(addedKey))
                        ||
                        (addedEntitiesDictionary.ContainsKey(addedKey))
                    )
                    {
                        throw new InvalidOperationException(string.Format("An entity with key {0} cannot be added, because entity with that key already exists", addedKey));
                    }

                    addedEntitiesDictionary.Add(addedKey, addedEntity);

                    var addedDoc = addedEntity.GetDocumentIfDirty();
                    // if this entity was already submitted and wasn't modified after that
                    if (addedDoc != null)
                    {
                        this.Log("Putting added entity with key {0}", addedKey);
                        entitiesToAdd.Add(addedKey, addedDoc);
                    }
                }

                // removing removed entities
                foreach (var removedKey in this._removedEntities)
                {
                    // if the entity was removed and then added anew - then it shouldn't be removed from the table
                    if (addedEntitiesDictionary.ContainsKey(removedKey))
                    {
                        continue;
                    }

                    this.Log("Removing entity with key {0}", removedKey);
                    entitiesToRemove.Add(removedKey);
                }
            }
            catch (Exception ex)
            {
                // executing a registered action, if any
                this.ThingsToDoUponSubmit.FireSafely(ex);
                this.ThingsToDoUponSubmit = null;
                throw;
            }

            // sending updates to DynamoDb and to cache
#if DEBUG
            var sw = new Stopwatch();
            sw.Start();
#endif

            // this stuff is here only because we want to keep this file free of async keyword
            return
                this.ExecuteUpdateBatchAsync(entitiesToAdd, entitiesToUpdate, entitiesToRemove)
                .ContinueWith
                (
                    updateTask => // and we can't use TaskContinuationOptions.OnlyOnRanToCompletion, because we don't want to return a cancelled task from this method 
                    {
                        // executing a registered action, if any
                        this.ThingsToDoUponSubmit.FireSafely(updateTask.Exception);
                        this.ThingsToDoUponSubmit = null;
                        if (updateTask.Exception != null)
                        {
                            throw updateTask.Exception;
                        }
#if DEBUG
                        sw.Stop();
                        this.Log("Batch update operation took {0} ms", sw.ElapsedMilliseconds);
#endif

                        // clearing the list of removed entities, as they should not be removed twice
                        this._removedEntities.Clear();

                        // clearing IsDirty-flag on all newly added entities (even those, that are not dirty)
                        foreach (var addedEntity in addedEntitiesDictionary.Values)
                        {
                            addedEntity.Commit();
                        }

                        // clearing IsDirty-flag on updated entities (even those, that are not dirty)
                        foreach (var updatedEntityWrapper in this._loadedEntities.Values)
                        {
                            updatedEntityWrapper.Commit();
                        }
                    }
                )
            ;
        }

        /// <summary>
        /// Registers an entity to be updated (for CUD operations)
        /// </summary>
        protected internal void AddUpdatedEntity(object newEntity, object oldEntity)
        {
            var key = this.EntityKeyGetter.GetKey(newEntity);

            EntityKey oldKey = null;
            if (oldEntity != null)
            {
                try
                {
                    oldKey = this.EntityKeyGetter.GetKey(oldEntity);
                }
                catch (Exception)
                {
                    oldKey = null;
                }
            }

            // if there's no previous key value - then a new entity is being added
            if (oldKey == null)
            {
                this._addedEntities.Add(new EntityWrapper(newEntity, this.ToDocumentConversionFunctor, this.EntityKeyGetter));
                return;
            }

            // checking that the key wasn't modified (which is not allowed)
            if (!key.Equals(oldKey))
            {
                throw new InvalidOperationException("Entity key cannot be edited");
            }

            this._loadedEntities[key] = new EntityWrapper(newEntity, this.ToDocumentConversionFunctor, this.EntityKeyGetter);
        }

        /// <summary>
        /// Returns a single entity by it's keys. Very useful in ASP.Net MVC
        /// </summary>
        protected internal object Find(params object[] keyValues)
        {
            if (this.KeyNames.Length != keyValues.Length)
            {
                throw new InvalidOperationException
                (
                    string.Format
                    (
                        "Table {0} has {1} key fields, but {2} key values was provided",
                        this.TableDefinition.TableName,
                        this.KeyNames.Length,
                        keyValues.Length
                    )
                );
            }

            // constructing a GET query
            var tr = new TranslationResult(this.TableEntityType.Name);
            for (int i = 0; i < keyValues.Length; i++)
            {
                var condition = new SearchCondition
                (
                    ScanOperator.Equal,
                    keyValues[i].ToDynamoDbEntry(keyValues[i].GetType())
                );
                tr.Conditions[this.KeyNames[i]] = new List<SearchCondition> { condition };
            }

            return this.LoadEntities(tr, this.TableEntityType);
        }

        /// <summary>
        /// Called by base class after creating a get/query/scan results reader
        /// </summary>
        /// <param name="reader"></param>
        protected override void InitReader(ISupervisableEnumerable reader)
        {
            base.InitReader(reader);

            // When an item is fetched from DynamoDb and enumerated - we need to keep a reference to it.
            // But only table entities should be registered. Projection types shouldn't.
            reader.EntityDocumentEnumerated += (entityDocument, entityWrapper) =>
            {
                EntityKey entityKey = null;

                // if this is the whole entity - then attaching it
                if (entityWrapper != null)
                {
                    entityKey = this.EntityKeyGetter.GetKey(entityDocument);
                    this._loadedEntities[entityKey] = entityWrapper;
                }

                // also putting the entity to cache
                var curIndexCreator = this.CurrentIndexCreator;
                if (curIndexCreator != null)
                {
                    curIndexCreator.AddEntityToIndex(entityKey, entityDocument);
                }
            };
        }

        /// <summary>
        /// Fills in and executes an update batch
        /// </summary>
        private Task ExecuteUpdateBatchAsync(IDictionary<EntityKey, Document> addedEntities, IDictionary<EntityKey, Document> modifiedEntities, ICollection<EntityKey> removedEntities)
        {
            var batch = this.TableDefinition.CreateBatchWrite();

            foreach (var key in addedEntities)
            {
                batch.AddDocumentToPut(key.Value);
            }
            foreach (var key in modifiedEntities)
            {
                batch.AddDocumentToPut(key.Value);
            }
            foreach (var key in removedEntities)
            {
                batch.AddKeyToDelete(this.EntityKeyGetter.GetKeyDictionary(key));
            }

            // Updating cache in current thread and DynamoDb in a separate thread
            var dynamoDbUpdateTask = batch.ExecuteAsync();

            // this should never throw any exceptions
            this.Cache.UpdateCacheAndIndexes(addedEntities, modifiedEntities, removedEntities);

            return dynamoDbUpdateTask.ContinueWith
            (
                updateTask =>
                {
                    if (updateTask.Exception != null)
                    {
                        // If the update failed - then removing all traces of modified entities from cache,
                        // as the update might partially succeed (in that case some of entities in cache might become stale)
                        this.Cache.RemoveEntities(addedEntities.Keys.Union(modifiedEntities.Keys).Union(removedEntities));

                        throw updateTask.Exception;
                    }
                }
            );
        }

        #endregion
    }
}
