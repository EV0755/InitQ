﻿using InitQ.Abstractions;
using InitQ.Cache;
using InitQ.Internal;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InitQ
{
    public class InitQCore
    {

        private async Task Send(IEnumerable<ConsumerExecutorDescriptor> ExecutorDescriptorList, IServiceProvider serviceProvider, InitQOptions options)
        {
            List<Task> tasks = new List<Task>();
            foreach (var ConsumerExecutorDescriptor in ExecutorDescriptorList)
            {
                //线程
                tasks.Add(Task.Run(async() =>
                {
                    using (var scope = serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope())
                    {
                        var publish = ConsumerExecutorDescriptor.Attribute.Name;
                        var provider = scope.ServiceProvider;
                        var obj = ActivatorUtilities.GetServiceOrCreateInstance(provider, ConsumerExecutorDescriptor.ImplTypeInfo);
                        ParameterInfo[] parameterInfos = ConsumerExecutorDescriptor.MethodInfo.GetParameters();
                        //redis对象
                        var _redis = scope.ServiceProvider.GetService<ICacheService>();
                        while (true)
                        {
                            try
                            {
                                if (options.ShowLog)
                                {
                                    Console.WriteLine($"执行方法:{obj.ToString()},key:{publish},执行时间{DateTime.Now}");
                                }
                                var count = await _redis.ListLengthAsync(publish);
                                if (count > 0)
                                {
                                    //从MQ里获取一条消息
                                    var res = await _redis.ListRightPopAsync(publish);
                                    if (string.IsNullOrEmpty(res)) continue;
                                    //堵塞
                                    await Task.Delay(options.IntervalTime);
                                    try
                                    {
                                        await Task.Run(async() =>
                                        {
                                            if (parameterInfos.Length == 0)
                                            {
                                                ConsumerExecutorDescriptor.MethodInfo.Invoke(obj, null);
                                            }
                                            else
                                            {
                                                object[] parameters = new object[] { res };
                                                ConsumerExecutorDescriptor.MethodInfo.Invoke(obj, parameters);
                                            }
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine(ex.Message);
                                    }
                                }
                                else
                                {
                                    //线程挂起1s
                                    await Task.Delay(options.SuspendTime);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.Message);
                            }
                        }
                    }
                }));
            }
            await Task.WhenAll(tasks);
        }


        private async Task SendDelay(IEnumerable<ConsumerExecutorDescriptor> ExecutorDescriptorList, IServiceProvider serviceProvider, InitQOptions options)
        {
            List<Task> tasks = new List<Task>();
            foreach (var ConsumerExecutorDescriptor in ExecutorDescriptorList)
            {
                //线程
                tasks.Add(Task.Run(async () =>
                {
                    using (var scope = serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope())
                    {
                        var publish = $"queue:{ConsumerExecutorDescriptor.Attribute.Name}";
                        var provider = scope.ServiceProvider;
                        var obj = ActivatorUtilities.GetServiceOrCreateInstance(provider, ConsumerExecutorDescriptor.ImplTypeInfo);
                        ParameterInfo[] parameterInfos = ConsumerExecutorDescriptor.MethodInfo.GetParameters();
                        //redis对象
                        var _redis = scope.ServiceProvider.GetService<ICacheService>();

                        //从zset添加到队列(锁)
                        tasks.Add(Task.Run(async () =>
                        {
                            while (true)
                            {
                                var keyInfo = "initq-lock:" + ConsumerExecutorDescriptor.Attribute.Name; //锁名称 每个延迟队列一个锁
                                var token = Guid.NewGuid().ToString("N"); //锁持有者
                                var coon = await _redis.GetDatabase().LockTakeAsync(keyInfo, token, TimeSpan.FromSeconds(5), CommandFlags.None);
                                if (coon)
                                {
                                    try
                                    {
                                        var dt = DateTime.Now;
                                        var arry = await _redis.SortedSetRangeByScoreAsync(ConsumerExecutorDescriptor.Attribute.Name, null, dt);
                                        if (arry != null && arry.Length > 0)
                                        {
                                            foreach (var item in arry)
                                            {
                                                await _redis.ListLeftPushAsync(publish, item);
                                            }
                                            //移除zset数据
                                            await _redis.SortedSetRemoveRangeByScoreAsync(ConsumerExecutorDescriptor.Attribute.Name, null, dt);
                                        }
                                        else
                                        {
                                            //线程挂起1s
                                            await Task.Delay(1000);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"执行延迟队列报错:{ex.Message}");
                                    }
                                    finally
                                    {
                                        //释放锁
                                        await _redis.GetDatabase().LockReleaseAsync(keyInfo, token);
                                    }
                                }
                                //else 
                                //{
                                //    //线程挂起1s,避免循环竞争锁,造成开销
                                //    await Task.Delay(1000);
                                //}
                            }
                        }));
                        //消费队列
                        tasks.Add(Task.Run(async () => 
                        {
                            while (true)
                            {
                                try
                                {
                                    if (options.ShowLog)
                                    {
                                        Console.WriteLine($"执行方法:{obj.ToString()},key:{publish},执行时间{DateTime.Now}");
                                    }
                                    var count = await _redis.ListLengthAsync(publish);
                                    if (count > 0)
                                    {
                                        //从MQ里获取一条消息
                                        var res = await _redis.ListRightPopAsync(publish);
                                        if (string.IsNullOrEmpty(res)) continue;
                                        //堵塞
                                        await Task.Delay(options.IntervalTime);
                                        try
                                        {
                                            await Task.Run(async () =>
                                            {
                                                if (parameterInfos.Length == 0)
                                                {
                                                    ConsumerExecutorDescriptor.MethodInfo.Invoke(obj, null);
                                                }
                                                else
                                                {
                                                    object[] parameters = new object[] { res };
                                                    ConsumerExecutorDescriptor.MethodInfo.Invoke(obj, parameters);
                                                }
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine(ex.Message);
                                        }
                                    }
                                    else
                                    {
                                        //线程挂起1s
                                        await Task.Delay(options.SuspendTime);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                }
                            }
                        }));
                    }
                }));
            }
            await Task.WhenAll(tasks);
        }

        public async Task FindInterfaceTypes(IServiceProvider provider, InitQOptions options)
        {
            var executorDescriptorList = new List<ConsumerExecutorDescriptor>();
            using (var scoped = provider.CreateScope())
            {
                var scopedProvider = scoped.ServiceProvider;
                var list_service = scopedProvider.GetService<Func<Type, IRedisSubscribe>>();
                foreach (var item in options.ListSubscribe)
                {
                    var consumerServices = list_service(item);
                    var typeInfo = consumerServices.GetType().GetTypeInfo();
                    if (!typeof(IRedisSubscribe).GetTypeInfo().IsAssignableFrom(typeInfo))
                    {
                        continue;
                    }
                    executorDescriptorList.AddRange(GetTopicAttributesDescription(typeInfo));
                }
                List<Task> tasks = new List<Task>();
                //普通队列任务
                tasks.Add(Send(executorDescriptorList.Where(m => m.Attribute.GetType().Name == "SubscribeAttribute"), provider, options));

                //延迟队列任务
                tasks.Add(SendDelay(executorDescriptorList.Where(m => m.Attribute.GetType().Name == "SubscribeDelayAttribute"), provider, options));
                await Task.WhenAll(tasks);
            }
        }




        private IEnumerable<ConsumerExecutorDescriptor> GetTopicAttributesDescription(TypeInfo typeInfo)
        {
            foreach (var method in typeInfo.DeclaredMethods)
            {
                var topicAttr = method.GetCustomAttributes<TopicAttribute>(true);
                var topicAttributes = topicAttr as IList<TopicAttribute> ?? topicAttr.ToList();

                if (!topicAttributes.Any())
                {
                    continue;
                }

                foreach (var attr in topicAttributes)
                {
                    yield return InitDescriptor(attr, method, typeInfo);
                }
            }
        }


        private ConsumerExecutorDescriptor InitDescriptor(TopicAttribute attr, MethodInfo methodInfo, TypeInfo implType)
        {
            var descriptor = new ConsumerExecutorDescriptor
            {
                Attribute = attr,
                MethodInfo = methodInfo,
                ImplTypeInfo = implType
            };

            return descriptor;
        }
    }
}
