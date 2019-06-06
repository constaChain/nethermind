/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Extensions;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Messages.Models;
using Nethermind.Logging;
using Nethermind.Network;
using Websocket.Client;

namespace Nethermind.EthStats.Integrations
{
    public class EthStatsIntegration : IEthStatsIntegration
    {
        private readonly string _name;
        private readonly string _node;
        private readonly int _port;
        private readonly string _network;
        private readonly string _protocol;
        private readonly string _api;
        private readonly string _client;
        private readonly string _contact;
        private readonly bool _canUpdateHistory;
        private readonly string _secret;
        private readonly IEthStatsClient _ethStatsClient;
        private readonly IMessageSender _sender;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        private bool _connected;
        private readonly DateTime _startedAt;

        public EthStatsIntegration(string name, string node, int port, string network, string protocol, string api,
            string client, string contact, bool canUpdateHistory, string secret, IEthStatsClient ethStatsClient,
            IMessageSender sender, IBlockProcessor blockProcessor, IPeerManager peerManager, ILogManager logManager)
        {
            _name = name;
            _node = node;
            _port = port;
            _network = network;
            _protocol = protocol;
            _api = api;
            _client = client;
            _contact = contact;
            _canUpdateHistory = canUpdateHistory;
            _secret = secret;
            _ethStatsClient = ethStatsClient;
            _sender = sender;
            _blockProcessor = blockProcessor;
            _peerManager = peerManager;
            _logger = logManager.GetClassLogger();
            _startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }

        public async Task InitAsync()
        {
            var exitEvent = new ManualResetEvent(false);
            using (var client = await _ethStatsClient.InitAsync())
            {
                if (_logger.IsDebug) _logger.Debug("Initial connection, sending 'hello' message...");
                await SendHelloAsync(client);
                _connected = true;
                client.ReconnectionHappened.Subscribe(async reason =>
                {
                    if (_logger.IsDebug) _logger.Debug("ETH Stats reconnected, sending 'hello' message...");
                    await SendHelloAsync(client);
                    _connected = true;
                });
                client.DisconnectionHappened.Subscribe(reason =>
                {
                    _connected = false;
                    if (_logger.IsDebug) _logger.Debug($"ETH Stats disconnected, reason: {reason}");
                });

                _blockProcessor.BlockProcessed += async (s, e) =>
                {
                    if (!_connected)
                    {
                        return;
                    }

                    await SendBlockAsync(client, e.Block);
                    await SendPendingAsync(client, e.Block.Transactions?.Length ?? 0);
                    await SendStatsAsync(client);
                };

                exitEvent.WaitOne();
            }
        }

        private Task SendHelloAsync(IWebsocketClient client)
            => _sender.SendAsync(client, new HelloMessage(_secret, new Info(_name, _node, _port, _network, _protocol,
                _api, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), _client,
                _contact, _canUpdateHistory)));

        private Task SendBlockAsync(IWebsocketClient client, Core.Block block)
            => _sender.SendAsync(client, new BlockMessage(new Block(block.Number, block.Hash?.ToString(),
                block.ParentHash?.ToString(),
                (long) block.Timestamp, block.Author?.ToString(), block.GasUsed, block.GasLimit,
                block.Difficulty.ToString(), block.TotalDifficulty?.ToString(),
                block.Transactions?.Select(t => new Transaction(t.Hash?.ToString())) ?? Enumerable.Empty<Transaction>(),
                block.TransactionsRoot.ToString(), block.StateRoot.ToString(),
                block.Ommers?.Select(o => new Uncle()) ?? Enumerable.Empty<Uncle>())));

        private Task SendPendingAsync(IWebsocketClient client, int pending)
            => _sender.SendAsync(client, new PendingMessage(new PendingStats(pending)));

        private Task SendStatsAsync(IWebsocketClient client)
            => _sender.SendAsync(client, new StatsMessage(new Messages.Models.Stats(true, true, false, 0,
                _peerManager.ActivePeers.Count, (long) 20.GWei(), 100)));
    }
}