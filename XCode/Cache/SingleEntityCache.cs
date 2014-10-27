﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NewLife.Log;
using NewLife.Reflection;
using NewLife.Threading;
using XCode.DataAccessLayer;

namespace XCode.Cache
{
    /// <summary>单对象缓存</summary>
    /// <remarks>
    /// 用一个值为实体的字典作为缓存（键一般就是主键），适用于单表大量互相没有关系的数据。
    /// 同时，AutoSave能够让缓存项在过期时自动保存数据，该特性特别适用于点击计数等场合。
    /// </remarks>
    /// <typeparam name="TKey">键值类型</typeparam>
    /// <typeparam name="TEntity">实体类型</typeparam>
    public class SingleEntityCache<TKey, TEntity> : CacheBase<TEntity>, ISingleEntityCache where TEntity : Entity<TEntity>, new()
    {
        #region 属性
        private Int32 _Expriod = CacheSetting.SingleCacheExpire;
        /// <summary>过期时间。单位是秒，默认60秒/600秒（独占数据库）</summary>
        public Int32 Expriod { get { return _Expriod; } set { _Expriod = value; } }

        private Int32 _MaxEntity = 10000;
        /// <summary>最大实体数。默认10000</summary>
        public Int32 MaxEntity { get { return _MaxEntity; } set { _MaxEntity = value; } }

        private Boolean _AutoSave = true;
        /// <summary>缓存到期时自动保存</summary>
        public Boolean AutoSave { get { return _AutoSave; } set { _AutoSave = value; } }

        private Boolean _AllowNull;
        /// <summary>允许缓存空对象</summary>
        public Boolean AllowNull { get { return _AllowNull; } set { _AllowNull = value; } }

        #region 主键

        private Boolean _MasterKeyUsingUniqueField = true;
        /// <summary>单对象缓存主键是否使用实体模型唯一键（第一个标识列或者唯一的主键）</summary>
        public Boolean MasterKeyUsingUniqueField
        {
            get { return _MasterKeyUsingUniqueField; }
            set { _MasterKeyUsingUniqueField = value; }
        }

        private Func<TEntity, TKey> _GetKeyMethod;
        /// <summary>获取缓存主键的方法，默认方法为获取实体主键值</summary>
        public Func<TEntity, TKey> GetKeyMethod
        {
            get
            {
                if (_GetKeyMethod == null)
                {
                    var fi = Entity<TEntity>.Meta.Unique;
                    if (fi != null)
                    {
                        _GetKeyMethod = entity => (TKey)entity[fi.Name];
                    }
                    //if (_GetKeyMethod == null)
                    //{
                    //	throw new ArgumentNullException("GetKeyMethod", "没有找到GetKey方法，请先设置获取缓存键的方法！");
                    //}
                }
                return _GetKeyMethod;
            }
            set { _GetKeyMethod = value; }
        }

        private Func<TKey, TEntity> _FindKeyMethod;
        /// <summary>查找数据的方法</summary>
        public Func<TKey, TEntity> FindKeyMethod
        {
            get
            {
                if (_FindKeyMethod == null)
                {
                    _FindKeyMethod = key => Entity<TEntity>.FindByKey(key);

                    if (_FindKeyMethod == null) throw new ArgumentNullException("FindKeyMethod", "没有找到FindByKey方法，请先设置查找数据的方法！");
                }
                return _FindKeyMethod;
            }
            set { _FindKeyMethod = value; }
        }

        #endregion

        #region 从键

        private Boolean _SlaveKeyIgnoreCase = false;
        /// <summary>从键是否区分大小写</summary>
        public Boolean SlaveKeyIgnoreCase
        {
            get { return _SlaveKeyIgnoreCase; }
            set { _SlaveKeyIgnoreCase = value; }
        }

        private Func<String, TEntity> _FindSlaveKeyMethod;
        /// <summary>根据从键查找数据的方法</summary>
        public Func<String, TEntity> FindSlaveKeyMethod
        {
            get { return _FindSlaveKeyMethod; }
            set { _FindSlaveKeyMethod = value; }
        }

        private Func<TEntity, String> _GetSlaveKeyMethod;
        /// <summary>获取缓存从键的方法，默认为空</summary>
        public Func<TEntity, String> GetSlaveKeyMethod
        {
            get { return _GetSlaveKeyMethod; }
            set { _GetSlaveKeyMethod = value; }
        }

        #endregion

        private Func _InitializeMethod;
        /// <summary>初始化缓存的方法，默认为空</summary>
        public Func InitializeMethod { get { return _InitializeMethod; } set { _InitializeMethod = value; } }

        private Boolean _HoldCache = CacheSetting.Alone;

        /// <summary>在数据修改时保持缓存，不再过期，独占数据库时默认打开，否则默认关闭</summary>
        public Boolean HoldCache
        {
            get { return _HoldCache; }
            set
            {
                _HoldCache = value;
                // 独占模式也需要用到定时器，否则无法自动保存
                //if (_HoldCache)
                //{
                //    if (_Timer != null) { _Timer.Dispose(); }
                //}
                //else
                //{
                //    StartTimer();
                //}
            }
        }

        private Boolean _Using;
        /// <summary>是否在使用缓存</summary>
        internal Boolean Using { get { return _Using; } private set { _Using = value; } }
        #endregion

        #region 构造、检查过期缓存
        TimerX _Timer = null;

        /// <summary>实例化一个实体缓存</summary>
        public SingleEntityCache()
        {
            // 启动一个定时器，用于定时清理过期缓存。因为比较耗时，最后一个参数采用线程池
            //timer = new TimerX(d => Check(), null, Expriod * 1000, Expriod * 1000, true);
        }

        /// <summary>启动一个定时器，用于定时清理过期缓存。因为比较耗时，最后一个参数采用线程池</summary>
        private void StartTimer()
        {
            // 独占模式下，不再自动清除缓存项
            //if (HoldCache) { return; }

            if (_Timer == null)
            {
                // 启动一个定时器
                var timer = new TimerX(d => Check(), null, Expriod * 1000, Expriod * 1000, true);
                if (Interlocked.CompareExchange<TimerX>(ref _Timer, timer, null) != null)
                {
                    timer.Dispose();
                    timer = null;
                }
            }
        }

        /// <summary>子类重载实现资源释放逻辑时必须首先调用基类方法</summary>
        /// <param name="disposing">从Dispose调用（释放所有资源）还是析构函数调用（释放非托管资源）。
        /// 因为该方法只会被调用一次，所以该参数的意义不太大。</param>
        protected override void OnDispose(bool disposing)
        {
            Clear("资源释放");

            if (_Timer != null) _Timer.Dispose();

            base.OnDispose(disposing);
        }

        /// <summary>定期检查实体，如果过期，则触发保存</summary>
        void Check()
        {
            // 独占缓存不删除缓存，仅判断自动保存
            if (HoldCache && !AutoSave) return;

            // 加锁后把缓存集合拷贝到数组中，避免后面遍历的时候出现多线程冲突
            CacheItem[] cs = null;
            if (Entities.Count <= 0) return;
            lock (Entities)
            {
                if (Entities.Count <= 0) return;

                cs = new CacheItem[Entities.Count];
                Entities.Values.CopyTo(cs, 0);
            }

            if (cs == null || cs.Length < 0) return;

            var list = new List<CacheItem>();
            foreach (var item in cs)
            {
                // 是否过期
                // 单对象缓存每次缓存的时候，设定一个将来的过期时间，然后以后只需要比较过期时间和当前时间就可以了
                if (item.ExpireTime <= DateTime.Now)
                {
                    if (item.Entity != null)
                    {
                        // 自动保存
                        if (AutoSave)
                        {
                            // 捕获异常，不影响别人
                            try
                            {
                                //item.Entity.Update();
                                // 需要在原连接名表名里面更新对象
                                AutoUpdate(item);
                            }
                            catch { }
                        }
                        item.Entity = null;
                    }
                    list.Add(item);
                }
            }
            // 独占缓存不删除缓存
            if (HoldCache) return;

            // 从缓存中删除，必须加锁
            if (list.Count > 0)
            {
                lock (Entities)
                {
                    foreach (var item in list)
                    {
                        //if (Entities.ContainsKey(item.Key)) Entities.Remove(item.Key);
                        Entities.Remove(item.Key);
                    }

                    Using = Entities.Count > 0;
                }
                if (SlaveEntities.Count > 0)
                {
                    lock (SlaveEntities)
                    {
                        foreach (var item in list)
                        {
                            if (!item.SlaveKey.IsNullOrWhiteSpace()) SlaveEntities.Remove(item.SlaveKey);
                        }
                    }
                }
            }
        }
        #endregion

        #region 缓存对象
        /// <summary>缓存对象</summary>
        class CacheItem
        {
            /// <summary>键</summary>
            internal TKey Key;

            /// <summary>从键</summary>
            internal String SlaveKey;

            /// <summary>实体</summary>
            internal TEntity Entity;

            /// <summary>缓存过期时间</summary>
            internal DateTime ExpireTime;
        }
        #endregion

        #region 单对象缓存

        /*
         * 从键类型暂没想好如何利用泛型，String类型是最常用的，硬性规定String类型，避免装箱拆箱的性能损耗
         */

        private Object _SyncRoot = new Object();

        //private SortedList<TKey, CacheItem> _Entities;
        //! Dictionary在集合方面具有较好查找性能，直接用字段，提高可能的性能
        /// <summary>单对象缓存</summary>
        private Dictionary<TKey, CacheItem> Entities = new Dictionary<TKey, CacheItem>();

        private Dictionary<String, CacheItem> _SlaveEntities;
        /// <summary>单对象缓存，从键查询使用</summary>
        private Dictionary<String, CacheItem> SlaveEntities
        {
            get
            {
                if (_SlaveEntities == null)
                {
                    Dictionary<String, CacheItem> dic;
                    if (SlaveKeyIgnoreCase)
                    {
                        dic = new Dictionary<String, CacheItem>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        dic = new Dictionary<String, CacheItem>();
                    }
                    if (Interlocked.CompareExchange<Dictionary<String, CacheItem>>(ref _SlaveEntities, dic, null) != null)
                    {
                        dic = null;
                    }
                }
                return _SlaveEntities;
            }
        }
        #endregion

        #region 统计
        /// <summary>总次数</summary>
        public Int32 Total;

        /// <summary>命中</summary>
        public Int32 Shoot;

        /// <summary>第一次命中，加锁之前</summary>
        public Int32 Shoot1;

        /// <summary>第二次命中，加锁之后</summary>
        public Int32 Shoot2;

        /// <summary>无效次数，不允许空但是查到对象又为空</summary>
        public Int32 Invalid;

        /// <summary>下一次显示时间</summary>
        public DateTime NextShow;

        /// <summary>显示统计信息</summary>
        public void ShowStatics()
        {
            if (Total > 0)
            {
                var sb = new StringBuilder();
                sb.AppendFormat("单对象缓存<{0},{1}>", typeof(TKey).Name, typeof(TEntity).Name);
                sb.AppendFormat("总次数{0}", Total);
                if (Shoot > 0) sb.AppendFormat("，数据命中{0}（{1:P02}）", Shoot, (Double)Shoot / Total);
                // 一级命中和总命中相等时不显示
                if (Shoot1 > 0 && Shoot1 != Shoot) sb.AppendFormat("，一级命中{0}（{1:P02}）", Shoot1, (Double)Shoot1 / Total);
                if (Shoot2 > 0) sb.AppendFormat("，二级命中{0}（{1:P02}）", Shoot2, (Double)Shoot2 / Total);
                if (Invalid > 0) sb.AppendFormat("，无效次数{0}（{1:P02}）", Invalid, (Double)Invalid / Total);

                XTrace.WriteLine(sb.ToString());
            }
        }
        #endregion

        #region 获取数据

        #region 批量主键获取

        /// <summary>根据主键获取实体记录列表</summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInKeys(IEnumerable<Int32> keys)
        {
            if (keys == null) { return new EntityList<TEntity>(); }

            var type = typeof(TKey);
            var skeys = keys.Select(e => (TKey)e.ChangeType(type));
            return FindAllInKeys(skeys);
        }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInKeys(IEnumerable<Int64> keys)
        {
            if (keys == null) { return new EntityList<TEntity>(); }

            var type = typeof(TKey);
            var skeys = keys.Select(e => (TKey)e.ChangeType(type));
            return FindAllInKeys(skeys);
        }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInKeys(IEnumerable<TKey> keys)
        {
            if (keys == null) { return new EntityList<TEntity>(); }

            var list = new EntityList<TEntity>(32);
            foreach (var key in keys)
            {
                var entity = GetItem(key);
                if (entity != null) { list.Add(entity); }
            }
            return list;
        }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <typeparam name="T">主键原始类型</typeparam>
        /// <param name="keys">主键字符串，以逗号或分号分割</param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInKeys<T>(String keys)
        {
            if (keys.IsNullOrWhiteSpace()) { return new EntityList<TEntity>(); }

            var srctype = typeof(T);
            var desttype = typeof(TKey);
            var kvs = keys.Split(new Char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (srctype == typeof(String))
            {
                return FindAllInKeys(kvs.Select(e => (TKey)e.ChangeType(desttype)));
            }
            else
            {
                return FindAllInKeys(kvs.Select(e => (T)e.ChangeType(srctype)).Select(e => (TKey)e.ChangeType(desttype)));
            }
        }

        #endregion

        #region 批量从键获取

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInSlaveKeys(IEnumerable<Int32> slavekeys)
        {
            if (slavekeys == null) { return new EntityList<TEntity>(); }
            var skeys = slavekeys.Select(e => "" + e);
            return FindAllInSlaveKeys(skeys);
        }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInSlaveKeys(IEnumerable<Int64> slavekeys)
        {
            if (slavekeys == null) { return new EntityList<TEntity>(); }
            var skeys = slavekeys.Select(e => "" + e);
            return FindAllInSlaveKeys(skeys);
        }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInSlaveKeys(IEnumerable<String> slavekeys)
        {
            var list = new EntityList<TEntity>();
            if (slavekeys != null)
            {
                foreach (var key in slavekeys)
                {
                    var entity = GetItemWithSlaveKey(key);
                    if (entity != null) { list.Add(entity); }
                }
            }
            return list;
        }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        public EntityList<TEntity> FindAllInSlaveKeys(String slavekeys, params String[] separators)
        {
            if (separators == null || separators.Length < 1) { separators = new String[] { ",", ";" }; }
            return FindAllInSlaveKeys(slavekeys.Split(separators, StringSplitOptions.RemoveEmptyEntries));
        }

        #endregion

        #region 主键获取
        /// <summary>根据主键获取实体数据</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public TEntity this[TKey key] { get { return GetItem(key); } }

        private TEntity GetItem(TKey key)
        {
            // 为空的key，直接返回null，不进行缓存查找
            if (key == null) return null;
            //if (Type.GetTypeCode(typeof(TKey)) == TypeCode.String)
            //{
            //    var value = key as String;
            //    if (value == String.Empty) return null;
            //}
            var svalue = key as String;
            if (svalue != null && svalue.Length == 0) return null;

            // 更新统计信息
            XCache.CheckShowStatics(ref NextShow, ref Total, ShowStatics);

            // 如果找到项，返回
            CacheItem item = null;
            // 如果TryGetValue获取成功，item为空说明同一时间别的线程已做删除操作
            if (Entities.TryGetValue(key, out item) && item != null)
            {
                Interlocked.Increment(ref Shoot1);
                // 下面的GetData里会判断过期并处理
                return GetData(item, key);
            }

            ClearUp();

            lock (_SyncRoot)
            {
                // 再次尝试获取
                if (Entities.TryGetValue(key, out item) && item != null)
                {
                    Interlocked.Increment(ref Shoot2);
                    return GetData(item, key);
                }
                var entity = Invoke(FindKeyMethod, key);
                if (entity != null || AllowNull)
                {
                    TryAddWithMasterKey(key, entity);
                }
                else
                {
                    Interlocked.Increment(ref Invalid);
                }
                return entity;
            }
        }

        private Boolean TryAddWithMasterKey(TKey masterKey, TEntity entity)
        {
            Using = true;

            var item = new CacheItem();
            String slaveKey = null;
            if (GetSlaveKeyMethod != null) { slaveKey = GetSlaveKeyMethod(entity); }
            item.Key = masterKey;
            item.SlaveKey = slaveKey;
            item.Entity = entity;
            item.ExpireTime = DateTime.Now.AddSeconds(Expriod);

            return TryAdd(masterKey, slaveKey, item);
        }
        #endregion

        #region 从键获取
        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        public TEntity GetItemWithSlaveKey(Int32 slaveKey)
        {
            return GetItemWithSlaveKey("" + slaveKey);
        }

        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        public TEntity GetItemWithSlaveKey(Int64 slaveKey)
        {
            return GetItemWithSlaveKey("" + slaveKey);
        }

        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        public TEntity GetItemWithSlaveKey(String slaveKey)
        {
            // 为空的key，直接返回null，不进行缓存查找
            if (slaveKey.IsNullOrWhiteSpace()) return null;
            if (FindSlaveKeyMethod == null) return null;

            // 更新统计信息
            XCache.CheckShowStatics(ref NextShow, ref Total, ShowStatics);

            // 如果找到项，返回
            CacheItem item = null;
            // 如果TryGetValue获取成功，item为空说明同一时间别的线程已做删除操作
            if (SlaveEntities.TryGetValue(slaveKey, out item) && item != null)
            {
                Interlocked.Increment(ref Shoot1);
                // 下面的GetData里会判断过期并处理
                return GetData(item, item.Key);
            }

            ClearUp();

            lock (_SyncRoot)
            {
                // 再次尝试获取
                if (SlaveEntities.TryGetValue(slaveKey, out item) && item != null)
                {
                    Interlocked.Increment(ref Shoot2);
                    return GetData(item, item.Key);
                }
                var entity = Invoke(FindSlaveKeyMethod, slaveKey);
                if (entity != null || AllowNull)
                {
                    TryAddWithSlaveKey(slaveKey, entity);
                }
                else
                {
                    Interlocked.Increment(ref Invalid);
                }
                return entity;
            }
        }

        private Boolean TryAddWithSlaveKey(String slaveKey, TEntity entity)
        {
            Using = true;

            var item = new CacheItem();
            var masterKey = GetKeyMethod(entity);
            item.Key = masterKey;
            item.SlaveKey = slaveKey;
            item.Entity = entity;
            item.ExpireTime = DateTime.Now.AddSeconds(Expriod);

            return TryAdd(masterKey, slaveKey, item);
        }
        #endregion

        /// <summary>添加实体对象到缓存</summary>
        /// <param name="masterKey"></param>
        /// <param name="slaveKey"></param>
        /// <param name="item"></param>
        /// <returns></returns>
        private Boolean TryAdd(TKey masterKey, String slaveKey, CacheItem item)
        {
            var success = false;
            lock (Entities)
            {
                if (!Entities.ContainsKey(masterKey))
                {
                    Entities.Add(masterKey, item);
                    success = true;
                }
            }
            if (success && !slaveKey.IsNullOrWhiteSpace())
            {
                lock (SlaveEntities)
                {
                    // 新增或更新
                    SlaveEntities[slaveKey] = item;
                }
            }
            return success;
        }

        /// <summary>内部处理返回对象。
        /// 把对象传进来，而不是只传键值然后查找，是为了避免别的线程移除该项
        /// </summary>
        /// <remarks>此方法只做更新操作，不再进行缓存新增操作</remarks>
        /// <param name="item"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private TEntity GetData(CacheItem item, TKey key)
        {
            if (item == null) return null;

            // 未过期，直接返回
            //if (HoldCache || item.ExpireTime > DateTime.Now)
            // 这里不能判断独占缓存，否则将失去自动保存的机会
            if (item.ExpireTime > DateTime.Now)
            {
                Interlocked.Increment(ref Shoot);
                return item.Entity;
            }

            // 自动保存
            AutoUpdate(item);

            // 判断别的线程是否已更新
            if (HoldCache || item.ExpireTime > DateTime.Now) { return item.Entity; }

            // 更新过期缓存，在原连接名表名里面获取
            var entity = Invoke(FindKeyMethod, key);
            if (entity != null || AllowNull)
            {
                item.Entity = entity;
                item.ExpireTime = DateTime.Now.AddSeconds(Expriod);
            }
            else
            {
                Interlocked.Increment(ref Invalid);
            }

            return entity;
        }

        ///// <summary>更新缓存</summary>
        ///// <param name="item"></param>
        ///// <param name="key"></param>
        ///// <returns></returns>
        //TEntity UpdateCache(CacheItem item, TKey key)
        //{
        //    // 在原连接名表名里面获取
        //    var entity = Invoke(FindKeyMethod, key);
        //    if (entity != null || AllowNull)
        //    {
        //        item.Entity = entity;
        //        item.ExpireTime = DateTime.Now.AddSeconds(Expriod);

        //        if (!Entities.ContainsKey(key)) Entities.Add(key, item);
        //    }
        //    else
        //    {
        //        Interlocked.Increment(ref Invalid);
        //    }

        //    return entity;
        //}

        /// <summary>清理缓存队列</summary>
        private void ClearUp()
        {
            var list = new List<CacheItem>();
            lock (Entities)
            {
                //队列满时，移除最老的一个
                while (Entities.Count >= MaxEntity)
                {
                    var first = RemoveFirst();
                    if (first != null && !first.SlaveKey.IsNullOrWhiteSpace()) { list.Add(first); }
                }
            }
            if (list.Count < 1) { return; }
            lock (SlaveEntities)
            {
                foreach (var item in list)
                {
                    SlaveEntities.Remove(item.SlaveKey);
                }
            }
        }

        /// <summary>移除第一个缓存项</summary>
        private CacheItem RemoveFirst()
        {
            var keyFirst = GetFirstKey();
            if (keyFirst != null && (Type.GetTypeCode(typeof(TKey)) != TypeCode.String || String.IsNullOrEmpty(keyFirst as String)))
            {
                CacheItem item = null;
                if (Entities.TryGetValue(keyFirst, out item) && item != null)
                {
                    if (Debug) DAL.WriteLog("单实体缓存{0}超过最大数量限制{1}，准备移除第一项{2}", typeof(TEntity).FullName, MaxEntity, keyFirst);

                    Entities.Remove(keyFirst);

                    //自动保存
                    AutoUpdate(item);

                    return item;
                }
            }
            return null;
        }

        /// <summary>获取第一个缓存项</summary>
        /// <returns></returns>
        private TKey GetFirstKey()
        {
            foreach (var item in Entities)
            {
                return item.Key;
            }
            return default(TKey);
        }

        /// <summary>自动更新，最主要是在原连接名和表名里面更新对象</summary>
        /// <param name="item"></param>
        private void AutoUpdate(CacheItem item)
        {
            if (item != null && AutoSave && item.Entity != null) Invoke(e => e.Entity.Update(), item);
        }
        #endregion

        #region 方法
        /// <summary>初始化单对象缓存，服务端启动时预载入实体记录集</summary>
        /// <remarks>注意事项：
        /// <para>调用方式：TEntity.Meta.Factory.Session.SingleCache.Initialize()，不要使用TEntity.Meta.Session.SingleCache.Initialize()；
        /// 因为Factory的调用会联级触发静态构造函数，确保单对象缓存设置成功</para>
        /// <para>服务端启动时，如果使用异步方式初始化单对象缓存，请将同一数据模型（ConnName）下的实体类型放在同一异步方法内执行，否则实体类型的架构检查抛异常</para>
        /// </remarks>
        public void Initialize()
        {
            if (HoldCache && InitializeMethod != null) { InitializeMethod(); }
        }

        /// <summary>是否包含指定键</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public Boolean ContainsKey(TKey key) { return Entities.ContainsKey(key); }

        /// <summary>是否包含指定从键</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public Boolean ContainsSlaveKey(Int32 key) { return SlaveEntities.ContainsKey("" + key); }

        /// <summary>是否包含指定从键</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public Boolean ContainsSlaveKey(Int64 key) { return SlaveEntities.ContainsKey("" + key); }

        /// <summary>是否包含指定从键</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public Boolean ContainsSlaveKey(String key) { return SlaveEntities.ContainsKey(key); }

        /// <summary>尝试从单对象缓存中获取与指定的主键关联的实体对象</summary>
        /// <param name="key">主键</param>
        /// <param name="entity">返回的实体对象</param>
        /// <returns>是否查找成功</returns>
        internal Boolean TryGetItem(TKey key, out TEntity entity)
        {
            CacheItem item;
            if (Entities.TryGetValue(key, out item) && item != null)
            {
                entity = item.Entity;
                return true;
            }
            else
            {
                entity = null;
                return false;
            }
        }

        /// <summary>向单对象缓存添加项</summary>
        /// <param name="value">实体对象</param>
        /// <returns></returns>
        public Boolean Add(TEntity value)
        {
            if (value == null) { return false; }
            var key = GetKeyMethod(value);
            return Add(key, value);
        }

        /// <summary>向单对象缓存添加项</summary>
        /// <param name="key"></param>
        /// <param name="value">数值</param>
        /// <returns></returns>
        public Boolean Add(TKey key, TEntity value)
        {
            // 如果找到项，返回
            CacheItem item = null;
            if (Entities.TryGetValue(key, out item) && item != null && item.ExpireTime > DateTime.Now) return false;

            // 加锁
            lock (_SyncRoot)
            {
                // 如果已存在并且过期，则复制
                // TryGetValue获取成功，Item为空说明同一时间另外线程做了删除操作
                if (Entities.TryGetValue(key, out item) && item != null)
                {
                    if (DateTime.Now <= item.ExpireTime) return false;
                    var old = item.Entity;
                    if (old != null)
                    {
                        if (old != value) old.CopyFrom(value);
                    }
                    else
                    {
                        item.Entity = value;
                    }
                    item.ExpireTime = DateTime.Now.AddSeconds(Expriod);

                    return false;
                }
                else
                {
                    return TryAddWithMasterKey(key, value);
                }
            }
        }

        /// <summary>移除指定项</summary>
        /// <param name="key"></param>
        public void RemoveKey(TKey key)
        {
            CacheItem item = null;
            if (!Entities.TryGetValue(key, out item)) return;
            lock (Entities)
            {
                if (!Entities.TryGetValue(key, out item)) return;

                AutoUpdate(item);

                Entities.Remove(key);

                Using = Entities.Count > 0;
            }
            if (item != null && !item.SlaveKey.IsNullOrWhiteSpace())
            {
                lock (SlaveEntities)
                {
                    SlaveEntities.Remove(item.SlaveKey);
                }
            }
        }

        /// <summary>根据主键移除指定项</summary>
        /// <param name="entity"></param>
        public void Remove(TEntity entity)
        {
            if (entity == null) { return; }
            var key = GetKeyMethod(entity);
            RemoveKey(key);
        }

        /// <summary>清除所有数据</summary>
        /// <param name="reason">清除缓存原因</param>
        public void Clear(String reason = null)
        {
            if (Debug) DAL.WriteLog("清空单对象缓存：{0} 原因：{1}", typeof(TEntity).FullName, reason);

            if (AutoSave)
            {
                // 加锁处理自动保存
                lock (Entities)
                {
                    foreach (var key in Entities)
                    {
                        var item = key.Value;
                        if (item == null || item.Entity == null) continue;

                        //item.Entity.Update();
                        AutoUpdate(item);
                    }
                }
            }

            lock (Entities)
            {
                Entities.Clear();
            }
            lock (SlaveEntities)
            {
                SlaveEntities.Clear();
            }

            Using = false;
        }
        #endregion

        #region ISingleEntityCache 成员
        /// <summary>获取数据</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        IEntity ISingleEntityCache.this[object key] { get { return GetItem((TKey)key); } }

        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        IEntity ISingleEntityCache.GetItemWithSlaveKey(Int32 slaveKey) { return GetItemWithSlaveKey("" + slaveKey); }

        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        IEntity ISingleEntityCache.GetItemWithSlaveKey(Int64 slaveKey) { return GetItemWithSlaveKey("" + slaveKey); }

        /// <summary>根据从键获取实体数据</summary>
        /// <param name="slaveKey"></param>
        /// <returns></returns>
        IEntity ISingleEntityCache.GetItemWithSlaveKey(String slaveKey) { return GetItemWithSlaveKey(slaveKey); }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInKeys(IEnumerable<Int32> keys) { return FindAllInKeys(keys); }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInKeys(IEnumerable<Int64> keys) { return FindAllInKeys(keys); }

        /// <summary>根据主键获取实体记录列表</summary>
        /// <typeparam name="T">主键原始类型</typeparam>
        /// <param name="keys">主键字符串，以逗号或分号分割</param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInKeys<T>(String keys) { return FindAllInKeys<T>(keys); }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInSlaveKeys(IEnumerable<Int32> slavekeys) { return FindAllInSlaveKeys(slavekeys); }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInSlaveKeys(IEnumerable<Int64> slavekeys) { return FindAllInSlaveKeys(slavekeys); }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInSlaveKeys(IEnumerable<String> slavekeys) { return FindAllInSlaveKeys(slavekeys); }

        /// <summary>根据从键获取实体记录列表</summary>
        /// <param name="slavekeys"></param>
        /// <returns></returns>
        IEntityList ISingleEntityCache.FindAllInSlaveKeys(String slavekeys) { return FindAllInSlaveKeys(slavekeys); }

        /// <summary>是否包含指定主键</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        Boolean ISingleEntityCache.ContainsKey(Object key) { return ContainsKey((TKey)key); }

        /// <summary>移除指定项</summary>
        /// <param name="key"></param>
        void ISingleEntityCache.RemoveKey(Object key) { RemoveKey((TKey)key); }

        /// <summary>移除指定项</summary>
        /// <param name="entity"></param>
        void ISingleEntityCache.Remove(IEntity entity) { Remove(entity as TEntity); }

        /// <summary>向单对象缓存添加项</summary>
        /// <param name="key"></param>
        /// <param name="value">实体对象</param>
        /// <returns></returns>
        Boolean ISingleEntityCache.Add(Object key, IEntity value)
        {
            var entity = value as TEntity;
            if (entity == null) { return false; }
            return Add((TKey)key, entity);
        }

        /// <summary>向单对象缓存添加项</summary>
        /// <param name="value">实体对象</param>
        /// <returns></returns>
        Boolean ISingleEntityCache.Add(IEntity value)
        {
            var entity = value as TEntity;
            if (entity == null) { return false; }
            var key = GetKeyMethod(entity);
            return Add(key, entity);
        }
        #endregion

        #region 辅助
        internal SingleEntityCache<TKey, TEntity> CopySettingFrom(SingleEntityCache<TKey, TEntity> ec)
        {
            this.Expriod = ec.Expriod;
            this.MaxEntity = ec.MaxEntity;
            this.AutoSave = ec.AutoSave;
            this.AllowNull = ec.AllowNull;
            this.HoldCache = ec.HoldCache;

            this.MasterKeyUsingUniqueField = ec.MasterKeyUsingUniqueField;
            //this.KeyIgnoreCase = ec.KeyIgnoreCase;
            this.GetKeyMethod = ec.GetKeyMethod;
            this.FindKeyMethod = ec.FindKeyMethod;

            this.SlaveKeyIgnoreCase = ec.SlaveKeyIgnoreCase;
            this.GetSlaveKeyMethod = ec.GetSlaveKeyMethod;
            this.FindSlaveKeyMethod = ec.FindSlaveKeyMethod;

            this.InitializeMethod = ec.InitializeMethod;

            return this;
        }
        #endregion
    }
}