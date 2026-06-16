# 部署与运�?

## 构建与部�?

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：`scripts/6.questionbank/2.deploy/start.sh`

## 配置�?

### 服务端口

- gRPC（端口见项目配置�?

### 数据�?

SQLite，数据文件：`data/sqlite/ruoyu_study_questionbank.db`

### 对象存储配置

```json
{
  "Oss": {
    "Endpoint": "ruoyu-seaweedfs:8333",
    "AccessKey": "seaweedfs_admin",
    "SecretKey": "seaweedfs_admin",
    "BucketName": "ruoyu-study"
  }
}
```
