﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Distributed;
using ZHWB.Domain.Repositories;
using ZHWB.Infrastructure.Log;
using ZHWB.Infrastructure.Cache;
using ZHWB.Infrastructure.MessageQueue;
using CSRedis;
using ZHWB.Infrastructure;
using ZHWB.Domain.Models;
using ZHWB.Infrastructure.MySQL;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using ZHWB.MessageHubs;
using ZHWB.Middleware;
using System.Security.Claims;

namespace ZHWB
{
    public class Startup
    {
        private readonly ILogger _logger;
        public Startup(IConfiguration configuration, ILogger<Startup> logger)
        {
            Configuration = configuration;
            _logger = logger;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            //容器注册
            
            services.AddTransient<IUserRoleRepository, UserRoleRepository>();
            services.AddTransient<IRoleRepository, RoleRepository>();
            services.AddTransient<IUserRepository, UserRepository>();


            services.AddTransient<IDataCaches<User>, DataCaches<User>>();
            services.AddTransient<IDataCaches<Role>, DataCaches<Role>>();
            services.AddTransient<IDataCaches<UserRole>, DataCaches<UserRole>>();
 
            services.AddTransient<IExceptionLogger, ExceptionLogger>();
            services.AddSingleton<IRabitMQHandler, RabitMQHandler>();//MQ独立实例内部维护连接池
            services.AddSingleton<IDataRepository, DataRepository>();//MySQL独立实例内部维护连接池
            //redis分布式缓存支持
            var csredis = new CSRedis.CSRedisClient(Configuration["Redis:Configuration"]);
            RedisHelper.Initialization(csredis);
            services.AddSingleton<IDistributedCache>(new Microsoft.Extensions.Caching.Redis.CSRedisCache(RedisHelper.Instance));//Redis独立实例内部维护连接池
            var secretKey = Configuration["JWTAuth:secretKey"];
            var signingKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secretKey));
            //服务接口JWT Bearer验证配置
            //用法:
            //JS:headers:{"Authorization": "Bearer "+token},
            //客户端:URL?access_token=token
            services.AddAuthentication(options =>
            {
                //TOKEN
                //不使用COOKIE
                // Identity made Cookie authentication the default.
                // However, we want JWT Bearer Auth to be the default.
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;

            }).AddJwtBearer(options =>
            {
                // Configure JWT Bearer Auth to expect our security key
                options.TokenValidationParameters =
                   new TokenValidationParameters
                   {
                       LifetimeValidator = (before, expires, token, param) =>
                       {
                           var res = expires > DateTime.UtcNow;
                           return res;
                       },
                       ValidateIssuer = true,//是否验证Issuer
                       ValidateAudience = true,//是否验证Audience
                       ValidateLifetime = true,//是否验证失效时间
                       ValidateIssuerSigningKey = true,//是否验证SecurityKey*/
                       ValidIssuer = Configuration["JWTAuth:issuer"],
                       ValidAudience = Configuration["JWTAuth:audience"],
                       IssuerSigningKey = signingKey
                   };
                // We have to hook the OnMessageReceived event in order to
                // allow the JWT authentication handler to read the access
                // token from the query string when a WebSocket or 
                // Server-Sent Events request comes in.
                options.Events = new JwtBearerEvents
                {
                    // Change to use Name as the user identifier for SignalR
                    // WARNING: This requires that the source of your JWT token 
                    // ensures that the Name claim is unique!
                    // If the Name claim isn't unique, users could receive messages 
                    // intended for a different user!
                    //signalR认证
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);
            services.AddSignalR();
            //跨域支持
            services.AddCors(options =>
            {
                options.AddPolicy("AllowAll",
                builder =>
                {
                    builder.AllowAnyOrigin();
                    builder.AllowAnyMethod();
                    builder.AllowAnyHeader();
                });
            });
            //services.AddSingleton<IUserIdProvider, NameUserIdProvider>();
        }
        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env,
        ILoggerFactory loggerFactory, IDistributedCache cache, IApplicationLifetime lifetime)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                //app.UseHsts();//HTTPS
            }
            //app.UseHttpsRedirection();
            app.UseStatusCodePages();
            app.UseStaticFiles();
            app.UseAuthentication();
            app.UseCookiePolicy();
            //增加对负载均衡代理支持
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
            lifetime.ApplicationStarted.Register(() =>
            {
                //IRabitMQHandler  mQHandler= app.ApplicationServices.GetService(typeof(IRabitMQHandler)) as IRabitMQHandler;
            });
            //signalR支持
            app.UseSignalR(routes =>
            {
                routes.MapHub<NotifyHub>("/notifyHub");
            });
            //添加权限中间件
            app.UsePermission();

            //USEMVC必须在最后执行
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
            app.UseCors("AllowAll");

        }
    }
}
