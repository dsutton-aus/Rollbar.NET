﻿#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace UnitTest.Rollbar.DTOs
{
    using global::Rollbar;
    using global::Rollbar.DTOs;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    [TestClass]
    [TestCategory("ClientFixture")]
    public class ClientFixture
    {
        private Client _client;

        [TestInitialize]
        public void SetupFixture()
        {
            this._client = new Client();
        }

        [TestCleanup]
        public void TearDownFixture()
        {
        }

        [TestMethod]
        public void ClientRenderedAsDictWhenEmpty()
        {
            Assert.AreEqual("{}", JsonConvert.SerializeObject(_client));
        }

        [TestMethod]
        public void ClientRendersArbitraryKeysCorrectly()
        {
            _client["test-key"] = "test-value";
            Assert.AreEqual("{\"test-key\":\"test-value\"}", JsonConvert.SerializeObject(_client));
        }

    }
}
