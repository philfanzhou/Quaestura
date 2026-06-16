# 部署指南

本文档包含了项目环境搭建、配置要求和 Docker 容器部署等指南，未来自动化部署脚本也将存放在此目�?(`src/Host/`) 中�?

## 1. 快速启�?

### 1.1 启动要求

1. **PostgreSQL**: 必须先启�?PostgreSQL 服务
2. **MinIO**: 必须先启�?MinIO 服务
3. **自动建库**: 程序启动时会自动创建数据库和表结�?

### 1.2 gRPC API 端口

- **gRPC**: `:50053`
- **gRPC-Web**: 通过 HTTP/1.1 访问 gRPC

### 1.3 配置�?

在应用程序（或通过环境变量）的 `appsettings.json` 中需要配置如下关键连接信息：

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=ruoyu_study_questionbank;Username=postgres;Password=postgres"
  },
  "Oss": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "BucketName": "ruoyu-study"
  }
}
```

## 2. Docker 环境部署

本项目依赖的第三方组件推荐使�?Docker 进行本地或测试环境部署�?

### 2.1 部署 PostgreSQL

```bash
docker run -d \
  --name postgres_questionbank \
  -e POSTGRES_DB=ruoyu_study_questionbank \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 \
  -v postgres_data:/var/lib/postgresql/data \
  postgres:16
```

### 2.2 部署 MinIO

```bash
docker run -d \
  --name minio_questionbank \
  -p 9000:9000 \
  -p 9001:9001 \
  -e MINIO_ROOT_USER=minioadmin \
  -e MINIO_ROOT_PASSWORD=minioadmin \
  -v minio_data:/data \
  minio/minio server /data --console-address ":9001"
```
