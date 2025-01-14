﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;


public class BTCPayWallet : IWallet, IDestinationProvider
{
    private readonly WalletRepository _walletRepository;
    private readonly BitcoinLikePayoutHandler _bitcoinLikePayoutHandler;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private readonly Services.Wallets.BTCPayWallet _btcPayWallet;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    public OnChainPaymentMethodData OnChainPaymentMethodData;
    public readonly DerivationStrategyBase DerivationScheme;
    public readonly ExplorerClient ExplorerClient;
    public readonly IBTCPayServerClientFactory BtcPayServerClientFactory;
    public WabisabiStoreSettings WabisabiStoreSettings;
    public readonly IUTXOLocker UtxoLocker;
    public readonly ILogger Logger;
    private static readonly BlockchainAnalyzer BlockchainAnalyzer = new();

    public BTCPayWallet(
        WalletRepository walletRepository,
        BitcoinLikePayoutHandler bitcoinLikePayoutHandler,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
        Services.Wallets.BTCPayWallet btcPayWallet,
        PullPaymentHostedService pullPaymentHostedService,
        OnChainPaymentMethodData onChainPaymentMethodData, 
        DerivationStrategyBase derivationScheme,
        ExplorerClient explorerClient, 
        BTCPayKeyChain keyChain,
        IBTCPayServerClientFactory btcPayServerClientFactory, 
        string storeId,
        WabisabiStoreSettings wabisabiStoreSettings, 
        IUTXOLocker utxoLocker,
        ILoggerFactory loggerFactory, 
        Smartifier smartifier,
        ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> bannedCoins)
    {
        KeyChain = keyChain;
        _walletRepository = walletRepository;
        _bitcoinLikePayoutHandler = bitcoinLikePayoutHandler;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        _btcPayWallet = btcPayWallet;
        _pullPaymentHostedService = pullPaymentHostedService;
        OnChainPaymentMethodData = onChainPaymentMethodData;
        DerivationScheme = derivationScheme;
        ExplorerClient = explorerClient;
        BtcPayServerClientFactory = btcPayServerClientFactory;
        StoreId = storeId;
        WabisabiStoreSettings = wabisabiStoreSettings;
        UtxoLocker = utxoLocker;
        _smartifier = smartifier;
        _bannedCoins = bannedCoins;
        Logger = loggerFactory.CreateLogger($"BTCPayWallet_{storeId}");
    }

    public string StoreId { get; set; }

    public string WalletName => StoreId;
    public bool IsUnderPlebStop => false;

    bool IWallet.IsMixable(string coordinator)
    {
        return OnChainPaymentMethodData?.Enabled is  true && WabisabiStoreSettings.Settings.SingleOrDefault(settings =>
            settings.Coordinator.Equals(coordinator))?.Enabled is  true && ((BTCPayKeyChain)KeyChain).KeysAvailable;
    }

    public IKeyChain KeyChain { get; }
    public IDestinationProvider DestinationProvider => this;

    public int AnonymitySetTarget => WabisabiStoreSettings.PlebMode? 2:  WabisabiStoreSettings.AnonymitySetTarget;
    public bool ConsolidationMode => !WabisabiStoreSettings.PlebMode && WabisabiStoreSettings.ConsolidationMode;
    public TimeSpan FeeRateMedianTimeFrame { get; } = TimeSpan.FromHours(KeyManager.DefaultFeeRateMedianTimeFrameHours);
    public bool RedCoinIsolation => !WabisabiStoreSettings.PlebMode &&WabisabiStoreSettings.RedCoinIsolation;
    public bool BatchPayments => WabisabiStoreSettings.PlebMode || WabisabiStoreSettings.BatchPayments;

    public async Task<bool> IsWalletPrivateAsync()
    {
      return !BatchPayments && await GetPrivacyPercentageAsync()>= 1;
    }

    public async Task<double> GetPrivacyPercentageAsync()
    {
        return GetPrivacyPercentage(await GetAllCoins(), AnonymitySetTarget);
    }

    public async Task<CoinsView> GetAllCoins()
    {
        await _savingProgress;
        
        var utxos = await _btcPayWallet.GetUnspentCoins(DerivationScheme);
        var utxoLabels = await GetUtxoLabels(_walletRepository, StoreId,utxos);
        await _smartifier.LoadCoins(utxos.ToList(), 1, utxoLabels);
        var coins = await Task.WhenAll(_smartifier.Coins.Where(pair => utxos.Any(data => data.OutPoint == pair.Key))
            .Select(pair => pair.Value));

        return new CoinsView(coins);
    }

    public double GetPrivacyPercentage(CoinsView coins, int privateThreshold)
    {
        var privateAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet >= privateThreshold).TotalAmount();
        var normalAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet < privateThreshold).TotalAmount();

        var privateDecimalAmount = privateAmount.ToDecimal(MoneyUnit.BTC);
        var normalDecimalAmount = normalAmount.ToDecimal(MoneyUnit.BTC);
        var totalDecimalAmount = privateDecimalAmount + normalDecimalAmount;

        var pcPrivate = totalDecimalAmount == 0M ? 1d : (double)(privateDecimalAmount / totalDecimalAmount);
        return pcPrivate;
    }
    
    private IRoundCoinSelector _coinSelector;
    public readonly Smartifier _smartifier;
    private readonly ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> _bannedCoins;

    public IRoundCoinSelector GetCoinSelector()
    {
        _coinSelector??= new BTCPayCoinjoinCoinSelector(this,  Logger );
        return _coinSelector;
    }

    public async Task<IEnumerable<SmartCoin>> GetCoinjoinCoinCandidatesAsync(string coordinatorName)
    {
        try
        {
            await _savingProgress;
        }
        catch (Exception e)
        {
        }
        try
        {
            if (IsUnderPlebStop)
            {
                return Array.Empty<SmartCoin>();
            }
            
            var utxos = await   _btcPayWallet.GetUnspentCoins(DerivationScheme, true, CancellationToken.None);
            var utxoLabels = await GetUtxoLabels(_walletRepository, StoreId,utxos);
            if (!WabisabiStoreSettings.PlebMode)
            {
                if (WabisabiStoreSettings.InputLabelsAllowed?.Any() is true)
                {

                    utxos = utxos.Where(data =>
                        utxoLabels.TryGetValue(data.OutPoint, out var opLabels) &&
                        opLabels.Any(
                            attachment => WabisabiStoreSettings.InputLabelsAllowed.Any(s => attachment.Id == s))).ToArray();
                }

                if (WabisabiStoreSettings.InputLabelsExcluded?.Any() is true)
                {
                    
                    utxos = utxos.Where(data =>
                        !utxoLabels.TryGetValue(data.OutPoint, out var opLabels) ||
                        opLabels.All(
                            attachment => WabisabiStoreSettings.InputLabelsExcluded.All(s => attachment.Id != s))).ToArray();
                }
            }

            if (WabisabiStoreSettings.PlebMode || !WabisabiStoreSettings.CrossMixBetweenCoordinators)
            {
                utxos = utxos.Where(data =>
                        !utxoLabels.TryGetValue(data.OutPoint, out var opLabels) ||
                        !opLabels.Any(attachment => attachment.Type == "coinjoin" && attachment.Data?.Value<string>("CoordinatorName")  == coordinatorName))
                    .ToArray();
            }

            var locks = await UtxoLocker.FindLocks(utxos.Select(data => data.OutPoint).ToArray());
            utxos = utxos.Where(data => !locks.Contains(data.OutPoint)).Where(data => data.Confirmations > 0).ToArray();
            if (_bannedCoins.TryGetValue(coordinatorName, out var bannedCoins))
            {
                var expired = bannedCoins.Where(pair => pair.Value < DateTimeOffset.Now).ToArray();
                foreach (var c in expired)
                {
                    bannedCoins.Remove(c.Key);

                }

                utxos = utxos.Where(data => !bannedCoins.ContainsKey(data.OutPoint)).ToArray();
            }
            await _smartifier.LoadCoins(utxos.Where(data => data.Confirmations>0).ToList(), 1, utxoLabels);
            
            var resultX =  await Task.WhenAll(_smartifier.Coins.Where(pair =>  utxos.Any(data => data.OutPoint == pair.Key))
                .Select(pair => pair.Value));

            foreach (SmartCoin c in resultX)
            {
                var utxo = utxos.Single(coin => coin.OutPoint == c.Outpoint);
                c.Height = new Height((uint) utxo.Confirmations);
            }
            
            return resultX;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not compute coin candidate");
            return Array.Empty<SmartCoin>();
        }
    }

    public static async Task<Dictionary<OutPoint, List<Attachment>>> GetUtxoLabels(WalletRepository walletRepository, string storeId ,ReceivedCoin[] utxos)
    {
        var walletTransactionsInfoAsync = await walletRepository.GetWalletTransactionsInfo(new WalletId(storeId, "BTC"),
            utxos.SelectMany(GetWalletObjectsQuery.Get).Distinct().ToArray());

        var utxoLabels = utxos.Select(coin =>
            {
                walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.Hash.ToString(), out var info1);
                walletTransactionsInfoAsync.TryGetValue(coin.Address.ToString(), out var info2);
                walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.ToString(), out var info3);
                var info = walletRepository.Merge(info1, info2, info3);
                if (info is null)
                {
                    return (coin.OutPoint, null);
                }

                return (coin.OutPoint, info.Attachments);
            }).Where(tuple => tuple.Attachments is not null)
            .ToDictionary(tuple => tuple.OutPoint, tuple => tuple.Attachments);
        return utxoLabels;
    }


    public async Task<IEnumerable<SmartTransaction>> GetTransactionsAsync()
    {
        return Array.Empty<SmartTransaction>();

    }


    public class CoinjoinData
    {
        public class CoinjoinDataCoin
        {
            public string Outpoint { get; set; }
            public decimal Amount { get; set; }
            public double AnonymitySet { get; set; }
            public string? PayoutId { get; set; }
        }
        public string Round { get; set; }
        public string CoordinatorName { get; set; }
        public string Transaction { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public CoinjoinDataCoin[] CoinsIn { get; set; } = Array.Empty<CoinjoinDataCoin>();
        public CoinjoinDataCoin[] CoinsOut { get; set; }= Array.Empty<CoinjoinDataCoin>();
    }

    private Task _savingProgress = Task.CompletedTask;

    public async Task RegisterCoinjoinTransaction(CoinJoinResult result, string coordinatorName)
    {
        _savingProgress = RegisterCoinjoinTransactionInternal(result, coordinatorName);
        await _savingProgress;
    }
    private async Task RegisterCoinjoinTransactionInternal(CoinJoinResult result, string coordinatorName)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Logger.LogInformation($"Registering coinjoin result for {StoreId}");
            
            var storeIdForutxo = WabisabiStoreSettings.PlebMode ||
                string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet)? StoreId: WabisabiStoreSettings.MixToOtherWallet;
            var client = await BtcPayServerClientFactory.Create(null, StoreId);
            BTCPayServerClient utxoClient = client;
            DerivationStrategyBase utxoDerivationScheme = DerivationScheme;
            if (storeIdForutxo != StoreId)
            {
                utxoClient = await BtcPayServerClientFactory.Create(null, storeIdForutxo);
                var pm  = await utxoClient.GetStoreOnChainPaymentMethod(storeIdForutxo, "BTC");
                utxoDerivationScheme = ExplorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);
            }
            var kp = await ExplorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
                WellknownMetadataKeys.AccountKeyPath);
            
            //mark the tx as a coinjoin at a specific coordinator
            var txObject = new AddOnChainWalletObjectRequest() {Id = result.UnsignedCoinJoin.GetHash().ToString(), Type = "tx"};

            var labels = new[]
            {
                new AddOnChainWalletObjectRequest() {Id = "coinjoin", Type = "label"},
                new AddOnChainWalletObjectRequest() {Id = coordinatorName, Type = "label"}
            };


            
            await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", txObject);
            if(storeIdForutxo != StoreId)
                await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", txObject);
            
            foreach (var label in labels)
            {
                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", label);
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
                {
                    Id = label.Id,
                    Type = label.Type
                }, CancellationToken.None);

                if (storeIdForutxo != StoreId)
                {await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", label);
                    await utxoClient.AddOrUpdateOnChainWalletLink(storeIdForutxo, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
                    {
                        Id = label.Id,
                        Type = label.Type
                    }, CancellationToken.None);
                }
            }

            List<(IndexedTxOut txout, Task<KeyPathInformation>)> scriptInfos = new();
            var payoutLabels = 
            result.HandledPayments.Select(pair =>
                new AddOnChainWalletObjectRequest() {Id = pair.Value.Identifier, Type = "payout"});

            if (payoutLabels.Any())
            {

                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC",
                    new AddOnChainWalletObjectRequest("label", "payout"));
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                    new AddOnChainWalletObjectLinkRequest("label", "payout"));


                foreach (var label in payoutLabels)
                {

                    await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", label);
                    await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                        new AddOnChainWalletObjectLinkRequest() {Id = label.Id, Type = label.Type},
                        CancellationToken.None);

                    await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", label,
                        new AddOnChainWalletObjectLinkRequest() {Id = "payout", Type = "label"},
                        CancellationToken.None);
                }
            }

            Dictionary<IndexedTxOut, PendingPayment> indexToPayment = new();
            foreach (var script in result.RegisteredOutputs)
            {
                var txout = result.UnsignedCoinJoin.Outputs.AsIndexedOutputs()
                    .Single(@out => @out.TxOut.ScriptPubKey == script);

                
                //this was not a mix to self, but rather a payment
                var isPayment = result.HandledPayments.Where(pair =>
                    pair.Key.ScriptPubKey == txout.TxOut.ScriptPubKey && pair.Key.Value == txout.TxOut.Value);
                if (isPayment.Any())
                {
                    indexToPayment.Add(txout, isPayment.First().Value);
                   continue;
                }

                scriptInfos.Add((txout, ExplorerClient.GetKeyInformationAsync(BlockchainAnalyzer.StdDenoms.Contains(txout.TxOut.Value)?utxoDerivationScheme:DerivationScheme, script)));
            }

            await Task.WhenAll(scriptInfos.Select(t => t.Item2));
            var scriptInfos2 = scriptInfos.Where(tuple => tuple.Item2.Result is not null).ToDictionary(tuple => tuple.txout.TxOut.ScriptPubKey);
            var smartTx = new SmartTransaction(result.UnsignedCoinJoin, new Height(HeightType.Unknown));
            result.RegisteredCoins.ForEach(coin =>
            {
                coin.HdPubKey.SetKeyState(KeyState.Used);
                coin.SpenderTransaction = smartTx;
                smartTx.TryAddWalletInput(coin);
            });
            result.RegisteredOutputs.ForEach(s =>
            {
                if (scriptInfos2.TryGetValue(s, out var si))
                {
                    var derivation = DerivationScheme.GetChild(si.Item2.Result.KeyPath).GetExtPubKeys().First()
                        .PubKey;
                    var hdPubKey = new HdPubKey(derivation, kp.Derive(si.Item2.Result.KeyPath).KeyPath,
                        SmartLabel.Empty,
                        KeyState.Used);
                    
                    var coin = new SmartCoin(smartTx, si.txout.N, hdPubKey);
                    smartTx.TryAddWalletOutput(coin);
                }
            });

            //
            // scriptInfos.ForEach(information =>
            // {
            //     var derivation = DerivationScheme.GetChild(information.Item2.Result.KeyPath).GetExtPubKeys().First()
            //         .PubKey;
            //     var hdPubKey = new HdPubKey(derivation, kp.Derive(information.Item2.Result.KeyPath).KeyPath,
            //         SmartLabel.Empty,
            //         KeyState.Used);
            //
            //     var coin = new SmartCoin(smartTx, information.txout.N, hdPubKey);
            //     smartTx.TryAddWalletOutput(coin);
            // });
            
            
            try
            {
                BlockchainAnalyzer.Analyze(smartTx);
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to analyze anonsets of tx {smartTx.GetHash()}");
            }


            foreach (SmartCoin smartTxWalletOutput in smartTx.WalletOutputs)
                {
                    var utxoObject = new AddOnChainWalletObjectRequest()
                    {
                        Id = smartTxWalletOutput.Outpoint.ToString(),
                        Type = "utxo"
                    };
                    if (BlockchainAnalyzer.StdDenoms.Contains(smartTxWalletOutput.TxOut.Value.Satoshi) && smartTxWalletOutput.AnonymitySet != 1)
                    {
                        
                        await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", new AddOnChainWalletObjectRequest( "utxo", smartTxWalletOutput.Outpoint.ToString())
                        {
                            Data = JObject.FromObject(new
                            {
                                smartTxWalletOutput.AnonymitySet
                            })
                        });
                        await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", new AddOnChainWalletObjectRequest( "label", $"anonset-{smartTxWalletOutput.AnonymitySet}"));

                        if (smartTxWalletOutput.AnonymitySet != 1)
                        {
                            await utxoClient.AddOrUpdateOnChainWalletLink(storeIdForutxo, "BTC", utxoObject, 
                                new AddOnChainWalletObjectLinkRequest() {Id =  $"anonset-{smartTxWalletOutput.AnonymitySet}", Type = "label"}, CancellationToken.None);

                        }
                    }
                }
                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC",
                    new AddOnChainWalletObjectRequest()
                    {
                        Id = result.RoundId.ToString(),
                        Type = "coinjoin",
                        Data = JObject.FromObject(
                            new CoinjoinData()
                            {
                                Round = result.RoundId.ToString(),
                                CoordinatorName = coordinatorName,
                                Transaction = result.UnsignedCoinJoin.GetHash().ToString(),
                                CoinsIn =   smartTx.WalletInputs.Select(coin => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    AnonymitySet = coin.AnonymitySet,
                                    PayoutId =  null,
                                    Amount = coin.Amount.ToDecimal(MoneyUnit.BTC),
                                    Outpoint = coin.Outpoint.ToString()
                                }).ToArray(),
                                CoinsOut =   smartTx.WalletOutputs.Select(coin => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    AnonymitySet = coin.AnonymitySet,
                                    PayoutId =  null,
                                    Amount = coin.Amount.ToDecimal(MoneyUnit.BTC),
                                    Outpoint = coin.Outpoint.ToString()
                                }).Concat(indexToPayment.Select(pair => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    Amount = pair.Key.TxOut.Value.ToDecimal(MoneyUnit.BTC),
                                    PayoutId = pair.Value.Identifier,
                                    Outpoint = new OutPoint(result.UnsignedCoinJoin, pair.Key.N).ToString()
                                })).ToArray()
                            })
                    });
                
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                    new AddOnChainWalletObjectLinkRequest() {Id = result.RoundId.ToString(), Type = "coinjoin"},
                    CancellationToken.None);
                stopwatch.Stop();
                
                Logger.LogInformation($"Registered coinjoin result for {StoreId} in {stopwatch.Elapsed}");

        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not save coinjoin progress!");
            // ignored
        }
    }


    public async Task UnlockUTXOs()
    {
        var client = await BtcPayServerClientFactory.Create(null, StoreId);
        var utxos = await client.GetOnChainWalletUTXOs(StoreId, "BTC");
        var unlocked = new List<string>();
        foreach (OnChainWalletUTXOData utxo in utxos)
        {

            if (await UtxoLocker.TryUnlock(utxo.Outpoint))
            {
                unlocked.Add(utxo.Outpoint.ToString());
            }
        }

        Logger.LogInformation($"unlocked utxos: {string.Join(',', unlocked)}");
    }

public async Task<IEnumerable<IDestination>> GetNextDestinationsAsync(int count, bool preferTaproot, bool mixedOutputs)
    {
        if (!WabisabiStoreSettings.PlebMode && !string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet) && mixedOutputs)
        {
            try
            {
                var mixClient = await BtcPayServerClientFactory.Create(null, WabisabiStoreSettings.MixToOtherWallet);
                var pm = await mixClient.GetStoreOnChainPaymentMethod(WabisabiStoreSettings.MixToOtherWallet,
                    "BTC");
                
               var deriv =   ExplorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);
               if (deriv.ScriptPubKeyType() == DerivationScheme.ScriptPubKeyType())
               {
                   return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
                       _btcPayWallet.ReserveAddressAsync(WabisabiStoreSettings.MixToOtherWallet, deriv, "coinjoin"))).ContinueWith(task => task.Result.Select(information => information.Address));
               }
            }
            
            catch (Exception e)
            {
                WabisabiStoreSettings.MixToOtherWallet = null;
            }
        }
        return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
            _btcPayWallet.ReserveAddressAsync(StoreId ,DerivationScheme, "coinjoin"))).ContinueWith(task => task.Result.Select(information => information.Address));
    }

    public async Task<IEnumerable<PendingPayment>> GetPendingPaymentsAsync( UtxoSelectionParameters roundParameters)
    {
        
        
        try
        {
           var payouts = (await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
           {
               States = new [] {PayoutState.AwaitingPayment},
               Stores = new []{StoreId},
               PaymentMethods = new []{"BTC"}
           })).Select(async data =>
           {
               
               var  claim = await _bitcoinLikePayoutHandler.ParseClaimDestination(new PaymentMethodId("BTC", PaymentTypes.BTCLike),
                   data.Destination, CancellationToken.None);

               if (!string.IsNullOrEmpty(claim.error) || claim.destination is not IBitcoinLikeClaimDestination bitcoinLikeClaimDestination )
               {
                   return null;
               }

               var payoutBlob = data.GetBlob(_btcPayNetworkJsonSerializerSettings);
               var value = new Money(payoutBlob.CryptoAmount.Value, MoneyUnit.BTC);
               if (!roundParameters.AllowedOutputAmounts.Contains(value) ||
                   !roundParameters.AllowedOutputScriptTypes.Contains(bitcoinLikeClaimDestination.Address.ScriptPubKey.GetScriptType()))
               {
                   return null;
               }
               return new PendingPayment()
               {
                   Identifier = data.Id,
                   Destination = bitcoinLikeClaimDestination.Address,
                   Value =value,
                   PaymentStarted = PaymentStarted(data.Id),
                   PaymentFailed = PaymentFailed(data.Id),
                   PaymentSucceeded = PaymentSucceeded(data.Id),
               };
           }).Where(payment => payment is not null).ToArray();
           return await Task.WhenAll(payouts);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return Array.Empty<PendingPayment>();
        }
    }

    private Action<(uint256 roundId, uint256 transactionId, int outputIndex)> PaymentSucceeded(string payoutId)
    {
        
        return tuple =>
            _pullPaymentHostedService.MarkPaid( new HostedServices.MarkPayoutRequest()
            {
                PayoutId = payoutId,
                State = PayoutState.InProgress,
                Proof = JObject.FromObject(new PayoutTransactionOnChainBlob()
                {
                    Candidates = new HashSet<uint256>()
                    {
                        tuple.transactionId
                    },
                    TransactionId = tuple.transactionId
                })
            });
    }

    private Action PaymentFailed(string payoutId)
    {
        return () =>
        {
            _pullPaymentHostedService.MarkPaid(new HostedServices.MarkPayoutRequest()
            {
                PayoutId = payoutId,
                State = PayoutState.AwaitingPayment
            });
        };
    }

    private Func<Task<bool>> PaymentStarted(string payoutId)
    {
        return async () =>
        {
            try
            {
                await _pullPaymentHostedService.MarkPaid( new HostedServices.MarkPayoutRequest()
                {
                    PayoutId = payoutId,
                    State = PayoutState.InProgress,
                    Proof = JObject.FromObject(new WabisabiPaymentProof())
                });
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        };
    }

    public class WabisabiPaymentProof
    {
        [JsonProperty("proofType")]
        public string ProofType { get; set; } = "Wabisabi";
        [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
        public uint256 TransactionId { get; set; }
        [JsonProperty(ItemConverterType = typeof(NBitcoin.JsonConverters.UInt256JsonConverter), NullValueHandling = NullValueHandling.Ignore)]
        public HashSet<uint256> Candidates { get; set; } = new HashSet<uint256>();
        public string Link { get; set; }
    }
}
