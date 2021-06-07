using System;
using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace CrossChainProxy_Contract
{
    [ContractPermission("*")]
    public class Proxy : SmartContract
    {
        [InitialValue("44baf1fac6dc465d6318e84911fd9bf536c5d6fd", ContractParameterType.ByteArray)]// little endian
        private static readonly byte[] CCMCScriptHash = default;

        [InitialValue("NYxb4fSZVKAz8YsgaPK2WkT3KcAE9b3Vag", ContractParameterType.Hash160)]
        private static readonly UInt160 InitOwner = default;

        private static readonly byte[] ProxyHashPrefix = new byte[] { 0x01, 0x01 };
        private static readonly byte[] AssetHashPrefix = new byte[] { 0x01, 0x02 };
        private static readonly byte[] AssetRelativeAccuracyPrefix = new byte[] { 0x01, 0x03 };
        private static readonly byte[] PauseKey = new byte[] { 0x01, 0x04 };
        private static readonly byte[] FromAssetHashMapKey = new byte[] { 0x01, 0x05 }; // "FromAssetList";
        private static readonly byte[] OperatorKey = new byte[] { 0x01, 0x06 };
        private static readonly string mapName = "asset";
        private static readonly StorageMap assetMap = new StorageMap(Storage.CurrentContext, mapName);

        // Events
        public static event Action<UInt160, byte[]> TransferOwnershipEvent;
        public static event Action<byte[], byte[], BigInteger, UInt160, byte[], BigInteger> LockEvent;
        public static event Action<byte[], ByteString, BigInteger> UnlockEvent;
        public static event Action<BigInteger, byte[]> BindProxyHashEvent;
        public static event Action<UInt160, BigInteger, byte[]> BindAssetHashEvent;
        public static event Action<byte[], UInt160> OnDeploy;
        public static event Action<object> notify;

        private static readonly BigInteger chainId = 11;

        public static void _deploy(object data, bool update)
        {
            Storage.Put(Storage.CurrentContext, OperatorKey, InitOwner);
            OnDeploy(OperatorKey, InitOwner);
        }

        public static bool Pause()
        {
            Assert(Runtime.CheckWitness(GetOperator()), "pause: CheckWitness failed!");
            Storage.Put(Storage.CurrentContext, PauseKey, new byte[] { 0x01 });
            return true;
        }

        public static bool Unpause()
        {
            Assert(Runtime.CheckWitness(GetOperator()), "pause: CheckWitness failed!");
            Storage.Delete(Storage.CurrentContext, PauseKey);
            return true;
        }

        public static bool IsPaused()
        {
            return Storage.Get(Storage.CurrentContext, PauseKey).Equals(new byte[] { 0x01 });
        }

        public static bool unlockTimeoutAsset(UInt160 assetHash, UInt160 toAddress, BigInteger amount)
        {
            Assert(isTimeout(), "Time not out yet");
            Assert(Runtime.CheckWitness(GetOperator()), "CheckWitness failed!");
            return (bool)Contract.Call(assetHash, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, toAddress, amount);
        }

        public static bool isTimeout()
        {
            ulong TimeoutPoint = 100; //TODO: 部署前讨论好解锁的高度， 并修改
            if (Runtime.Time >= TimeoutPoint)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static bool TransferOwnership(byte[] newOperator)
        {
            Assert(newOperator.Length == 20, "transferOwnership: newOperator.Length != 20");
            UInt160 operator_ = (UInt160)Storage.Get(Storage.CurrentContext, OperatorKey);
            Assert(Runtime.CheckWitness(operator_), "transferOwnership: CheckWitness failed!");
            Storage.Put(Storage.CurrentContext, OperatorKey, newOperator);
            TransferOwnershipEvent(operator_, newOperator);
            return true;
        }

        public static UInt160 GetOperator()
        {
            return (UInt160)Storage.Get(Storage.CurrentContext, OperatorKey);
        }

        // check payable
        public static bool GetPaymentStatus() => ((BigInteger)assetMap.Get("enable")).Equals(1);

        // enable payment
        public static void EnablePayment()
        {
            Assert(Runtime.CheckWitness((UInt160)Storage.Get(Storage.CurrentContext, OperatorKey)), "No authorization.");
            assetMap.Put("enable", 1);
        }

        // disable payment
        public static void DisablePayment()
        {
            Assert(Runtime.CheckWitness((UInt160)Storage.Get(Storage.CurrentContext, OperatorKey)), "No authorization.");
            assetMap.Put("enable", 0);
        }


        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            if (GetPaymentStatus())
            {
                return;
            }
            else
            {
                throw new Exception("Payment is disable on this contract!");
            }
        }

        // add target proxy contract hash according to chain id into contract storage
        public static bool BindProxyHash(BigInteger toChainId, byte[] toProxyHash)
        {
            Assert(toChainId != chainId, "bindProxyHash: toChainId is negative or equal to 44.");
            Assert(toProxyHash.Length > 0, "bindProxyHash: toProxyHash.Length == 0");
            UInt160 operator_ = (UInt160)Storage.Get(Storage.CurrentContext, OperatorKey);
            Assert(Runtime.CheckWitness(operator_), (ByteString)("bindProxyHash: CheckWitness failed, ".ToByteArray().Concat(operator_)));
            Storage.Put(Storage.CurrentContext, ProxyHashPrefix.Concat(padRight(toChainId.ToByteArray(), 8)), toProxyHash);
            BindProxyHashEvent(toChainId, toProxyHash);
            return true;
        }

        // add target asset contract hash according to local asset hash & chain id into contract storage
        public static bool BindAssetHash(UInt160 fromAssetHash, BigInteger toChainId, byte[] toAssetHash, BigInteger relativeAccuracy)
        {
            Assert(toChainId != chainId, "bindAssetHash: toChainId cannot be negative or equal to 44.");

            UInt160 operator_ = (UInt160)Storage.Get(Storage.CurrentContext, OperatorKey);
            Assert(Runtime.CheckWitness(operator_), (ByteString)("bindAssetHash: CheckWitness failed! ".ToByteArray().Concat(operator_)));

            // Add fromAssetHash into storage so as to be able to be transferred into newly upgraded contract
            Assert(AddFromAssetHash(fromAssetHash), "bindAssetHash: addFromAssetHash failed!");
            Storage.Put(Storage.CurrentContext, AssetHashPrefix.Concat(fromAssetHash).Concat(padRight(toChainId.ToByteArray(), 8)), toAssetHash);
            Storage.Put(Storage.CurrentContext, AssetRelativeAccuracyPrefix.Concat(fromAssetHash).Concat(padRight(toChainId.ToByteArray(), 8)), relativeAccuracy);
            BindAssetHashEvent(fromAssetHash, toChainId, toAssetHash);
            return true;
        }

        private static bool AddFromAssetHash(UInt160 newAssetHash)
        {
            Map<UInt160, bool> assetHashMap = new Map<UInt160, bool>();
            ByteString assetHashMapInfo = Storage.Get(Storage.CurrentContext, FromAssetHashMapKey);
            if (assetHashMapInfo is null)
            {
                assetHashMap[newAssetHash] = true;
            }
            else
            {
                assetHashMap = (Map<UInt160, bool>)StdLib.Deserialize(assetHashMapInfo);
                if (!assetHashMap.HasKey(newAssetHash))
                {
                    assetHashMap[newAssetHash] = true;
                }
                else
                {
                    return true;
                }
            }
            // Make sure fromAssetHash has balanceOf method
            BigInteger balance = GetAssetBalance(newAssetHash);
            Storage.Put(Storage.CurrentContext, FromAssetHashMapKey, StdLib.Serialize(assetHashMap));
            return true;
        }

        public static BigInteger GetAssetBalance(UInt160 assetHash)
        {
            UInt160 curHash = Runtime.ExecutingScriptHash;
            BigInteger balance = (BigInteger)Contract.Call(assetHash, "balanceOf", CallFlags.All, new object[] { curHash });
            return balance;
        }

        // get target proxy contract hash according to chain id
        public static UInt160 GetProxyHash(BigInteger toChainId)
        {
            return (UInt160)Storage.Get(Storage.CurrentContext, ProxyHashPrefix.Concat((padRight(toChainId.ToByteArray(), 8))));
        }

        // get target asset contract hash according to local asset hash & chain id
        public static UInt160 GetAssetHash(byte[] fromAssetHash, BigInteger toChainId)
        {
            return (UInt160)Storage.Get(Storage.CurrentContext, AssetHashPrefix.Concat(fromAssetHash).Concat(padRight(toChainId.ToByteArray(), 8)));
        }

        public static BigInteger GetAssetRelativeAccuracy(byte[] fromAssetHash, BigInteger toChainId)
        {
            var rawAccuracy = Storage.Get(Storage.CurrentContext, AssetRelativeAccuracyPrefix.Concat(fromAssetHash).Concat(padRight(toChainId.ToByteArray(), 8)));
            return (rawAccuracy is null ? 1 : (BigInteger)rawAccuracy);
        }

        // used to lock asset into proxy contract
        public static bool Lock(byte[] fromAssetHash, byte[] fromAddress, BigInteger toChainId, byte[] toAddress, BigInteger amount)
        {
            Assert(GetPaymentStatus(), "Payment disable.");
            Assert(fromAssetHash.Length == 20, "lock: fromAssetHash SHOULD be 20-byte long.");
            Assert(fromAddress.Length == 20, "lock: fromAddress SHOULD be 20-byte long.");
            Assert(amount > 0, "lock: amount SHOULD be greater than 0.");
            Assert(!fromAddress.Equals(Runtime.ExecutingScriptHash), "lock: can not lock self");
            Assert(!IsPaused(), "lock: proxy is locked");

            // get the proxy contract on target chain
            var toProxyHash = GetProxyHash(toChainId);

            // get the corresbonding asset on to chain
            var toAssetHash = GetAssetHash(fromAssetHash, toChainId);
            var Params = new object[] { fromAddress, Runtime.ExecutingScriptHash, amount, null };
            // transfer asset from fromAddress to proxy contract address, use dynamic call to call nep5 token's contract "transfer"
            bool success = (bool)Contract.Call((UInt160)fromAssetHash, "transfer", CallFlags.All, Params);
            Assert(success, "lock: Failed to transfer NEP5 token to Nep5Proxy.");
            amount = amount * GetAssetRelativeAccuracy(fromAssetHash, toChainId);
            // construct args for proxy contract on target chain
            var inputArgs = SerializeArgs((byte[])toAssetHash, toAddress, amount);
            // dynamic call CCMC
            success = (bool)Contract.Call((UInt160)CCMCScriptHash, "crossChain", CallFlags.All, new object[] { toChainId, toProxyHash, "unlock", inputArgs, Runtime.ExecutingScriptHash });
            Assert(success, "lock: Failed to call CCMC.");
            // Validate payable
            LockEvent(fromAssetHash, fromAddress, toChainId, toAssetHash, toAddress, amount);

            return true;
        }

        // Methods of actual execution, used to unlock asset from proxy contract
        public static bool Unlock(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId)
        {
            //only allowed to be called by CCMC
            Assert(Runtime.CallingScriptHash == (UInt160)CCMCScriptHash, "unlock: Only allowed to be called by CCMC.");
            UInt160 proxyHash = GetProxyHash(fromChainId);
            // check the fromContract is stored, so we can trust it
            if ((ByteString)fromProxyContract != (ByteString)proxyHash)
            {
                notify("From proxy contract not found.");
                notify((ByteString)fromProxyContract);
                notify(proxyHash);
                return false;
            }

            Assert(!IsPaused(), "lock: proxy is locked");

            // parse the args bytes constructed in source chain proxy contract, passed by multi-chain
            object[] results = DeserializeArgs(inputBytes);
            var toAssetHash = (byte[])results[0];
            var toAddress = (UInt160)results[1];
            var amount = (BigInteger)results[2];
            amount = amount / GetAssetRelativeAccuracy(toAssetHash, fromChainId);
            Assert(toAssetHash.Length == 20, "unlock: ToChain Asset script hash SHOULD be 20-byte long.");
            Assert(toAddress.Length == 20, "unlock: ToChain Account address SHOULD be 20-byte long.");
            Assert(amount > 0, "ToChain Amount SHOULD be greater than 0.");

            var Params = new object[] { Runtime.ExecutingScriptHash, toAddress, amount, null };
            // transfer asset from proxy contract to toAddress
            bool success = (bool)Contract.Call((UInt160)toAssetHash, "transfer", CallFlags.All, Params);
            Assert(success, "unlock: Failed to transfer NEP5 token From Nep5Proxy to toAddress.");
            UnlockEvent(toAssetHash, toAddress, amount);
            return true;
        }

        // used to upgrade this proxy contract
        public static bool Update(ByteString nefFile, string manifest)
        {
            Assert(Runtime.CheckWitness((UInt160)Storage.Get(Storage.CurrentContext, OperatorKey)), "upgrade: CheckWitness failed!");
            ContractManagement.Update(nefFile, manifest);
            return true;
        }

        public static object[] DeserializeArgs(byte[] buffer)
        {
            var offset = 0;
            ByteString assetAddress;
            ByteString toAddress;
            BigInteger amount;
            (assetAddress, offset) = ReadVarBytes(buffer, offset);
            (toAddress, offset) = ReadVarBytes(buffer, offset);
            (amount, offset) = ReadUint255(buffer, offset);
            return new object[] { assetAddress, toAddress, amount };
        }

        public static (BigInteger, int) ReadUint255(byte[] buffer, int offset)
        {
            if (offset + 32 > buffer.Length)
            {
                notify("Length is not long enough!");
                throw new ArgumentException();
            }
            BigInteger result = new BigInteger(buffer.Range(offset, 32));
            offset = offset + 32;
            Assert(result >= 0, "result should > 0");//uint255 exceed max size, can not concat 0x00
            return (result, offset);
        }

        // return [BigInteger: value, int: offset]
        public static (BigInteger, int) ReadVarInt(byte[] buffer, int offset)
        {
            (ByteString fb, int newOffset) = ReadBytes(buffer, offset, 1); // read the first byte
            if (fb.Equals((ByteString)new byte[] { 0xfd }))
            {
                return (new BigInteger(buffer.Range(newOffset, 2).Concat(new byte[] { 0x00 })), newOffset + 2);
            }
            else if (fb.Equals((ByteString)new byte[] { 0xfe }))
            {
                return (new BigInteger(buffer.Range(newOffset, 4).Concat(new byte[] { 0x00 })), newOffset + 4);
            }
            else if (fb.Equals((ByteString)new byte[] { 0xff }))
            {
                return (new BigInteger(buffer.Range(newOffset, 8).Concat(new byte[] { 0x00 })), newOffset + 8);
            }
            else
            {
                return (new BigInteger(((byte[])fb).Concat(new byte[] { 0x00 })), newOffset);
            }
        }

        // return [byte[], new offset]
        public static (ByteString, int) ReadVarBytes(byte[] buffer, int offset)
        {
            BigInteger count;
            int newOffset;
            (count, newOffset) = ReadVarInt(buffer, offset);
            return ReadBytes(buffer, newOffset, (int)count);
        }

        // return [byte[], new offset]
        public static (ByteString, int) ReadBytes(byte[] buffer, int offset, int count)
        {
            if (offset + count > buffer.Length)
            {
                notify("read Bytes fail");
                throw new ArgumentException();
            }
            return ((ByteString)buffer.Range(offset, count), offset + count);
        }

        public static byte[] SerializeArgs(byte[] assetHash, byte[] address, BigInteger amount)
        {
            var buffer = new byte[] { };
            buffer = WriteVarBytes(assetHash, buffer);
            buffer = WriteVarBytes(address, buffer);
            buffer = WriteUint255(amount, buffer);
            return buffer;
        }

        public static byte[] WriteUint255(BigInteger value, byte[] source)
        {
            Assert(value >= 0, "Value out of range of uint255.");
            var v = padRight(value.ToByteArray(), 32);
            return source.Concat(v); // no need to concat length, fix 32 bytes
        }

        public static byte[] writeVarInt(BigInteger value, byte[] source)
        {
            if (value < 0)
            {
                notify("WVI: value should be positive");
                throw new ArgumentException();
            }
            else if (value < 0xFD)
            {
                var v = padRight(value.ToByteArray().Take(1), 1);
                return source.Concat(v);
            }
            else if (value <= 0xFFFF) // 0xff, need to pad 1 0x00
            {
                byte[] length = new byte[] { 0xFD };
                var v = padRight(value.ToByteArray().Take(2), 2);
                return source.Concat(length).Concat(v);
            }
            else if (value <= 0XFFFFFFFF) //0xffffff, need to pad 1 0x00 
            {
                byte[] length = new byte[] { 0xFE };
                var v = padRight(value.ToByteArray().Take(4), 4);
                return source.Concat(length).Concat(v);
            }
            else //0x ff ff ff ff ff, need to pad 3 0x00
            {
                byte[] length = new byte[] { 0xFF };
                var v = padRight(value.ToByteArray().Take(8), 8);
                return source.Concat(length).Concat(v);
            }
        }

        private static byte[] WriteVarBytes(byte[] value, byte[] Source)
        {
            return writeVarInt(value.Length, Source).Concat(value);
        }

        // add padding zeros on the right
        private static byte[] padRight(byte[] value, int length)
        {
            var l = value.Length;
            if (l > length)
            {
                notify("size exceed");
                throw new ArgumentException();
            }
            for (int i = 0; i < length - l; i++)
            {
                value = value.Concat(new byte[] { 0x00 });
            }
            return value;
        }

        /// <summary>
        /// Tested, 将Biginteger转换成byteArray, 会在首位添加符号位0x00
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        public static byte[] ConvertBigintegerToByteArray(BigInteger number)
        {
            var temp = (byte[])(object)number;
            byte[] vs = temp.ToByteString().ToByteArray().Reverse();//ToByteString修改虚拟机类型， Reverse转换端序
            return vs;
        }

        private static void Assert(bool condition, string msg)
        {
            if (!condition)
            {
                notify((ByteString)"Nep5Proxy ".ToByteArray().Concat(msg.ToByteArray()));
                throw new InvalidOperationException(msg);
            }
        }
    }
}
