# 部署指南

本文档包含了项目环境搭建、配置要求和 Docker 容器部署等指南。

## 1. 快速启动

### 1.1 启动要求

1. **SeaweedFS**: 必须先启动 SeaweedFS 服务 (S3 端口 8333)
2. **自动建库**: 程序启动时会自动创建 SQLite 数据库和表结构

### 1.2 gRPC API 端口

- **gRPC**: 见 `PROJECT.md` 端口分配表
- **gRPC-Web**: 通过 HTTP/1.1 访问 gRPC

### 1.3 配置项

在应用程序（或通过环境变量）的 `appsettings.json` 中需要配置如下关键连接信息：

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=data/sqlite/ruoyu_study_questionbank.db"
  },
  "Oss": {
    "Endpoint": "localhost:8333",
    "AccessKey": "mock_access_key",
    "SecretKey": "mock_secret_key",
    "BucketName": "ruoyu-study",
    "PublicEndpoint": "https://ry.zhoufan.asia"
  }
}
```

## 2. Docker 环境部署

本项目依赖的第三方组件推荐使用 Docker 进行本地或测试环境部署。

### 2.1 部署 SeaweedFS

SeaweedFS 部署脚本见仓库根目录 `script/env-script/02-seaweedfs/start.sh`。

### 2.2 构建与部署

使用仓库统一的构建脚本：

```bash
./script/build-script/06-questionbank.build.sh
```

部署网络：`ruoyu-net` 桥接网络，所有容器通过容器名解析通信。
