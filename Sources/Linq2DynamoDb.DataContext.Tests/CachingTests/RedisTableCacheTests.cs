﻿using System;
using Linq2DynamoDb.DataContext.Caching.Redis;
using Linq2DynamoDb.DataContext.Utils;
using NUnit.Framework;
using StackExchange.Redis;

namespace Linq2DynamoDb.DataContext.Tests.CachingTests
{
    public class RedisTableCacheTests : TableCacheTestsBase
    {
        private ConnectionMultiplexer _redisConn;

        public override void SetUp()
        {
            string redisAddress = "localhost:6379";
            var config = new ConfigurationOptions();
            config.EndPoints.Add(redisAddress);
            config.AllowAdmin = true;
            this._redisConn = ConnectionMultiplexer.Connect(config);
            this._redisConn.GetServer(redisAddress).FlushAllDatabases();

            this.TableCache = new RedisTableCache(this._redisConn, TimeSpan.FromSeconds(10));
            base.SetUp();
        }


        protected override void DropIndexEntityFromCache(string indexKey)
        {
            indexKey = "{Books}:" + indexKey;
            bool success = this._redisConn.GetDatabase().KeyDelete(indexKey);
            Assert.IsTrue(success, "The index wasn't dropped from cache. Check the key format.");
        }

    }
}
