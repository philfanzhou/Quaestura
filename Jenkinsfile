// Ruoyu.Study - QuestionBank Service Pipeline
//
// Per-service pipeline for ruoyu.questionBank:
//   1. Preflight — verify docker + dotnet + repo access
//   2. Build    — reuse script/build-script/06-questionbank.build.sh (docker build)
//   3. Deploy   — restart the ruoyu-questionbank container via start.sh
//   4. Smoke    — health check via ruoyu-net (no host port mapping)
//
// No Unit Test stage (no test project found).

pipeline {
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
        disableConcurrentBuilds()
    }

    environment {
        REPO_DIR         = '/mnt/data1/Ruoyu.Study'
        SERVICE_DIR      = "${env.REPO_DIR}/src/services/ruoyu.questionBank"
        BUILD_SCRIPT     = "${env.REPO_DIR}/script/build-script/06-questionbank.build.sh"
        START_SCRIPT     = "${env.SERVICE_DIR}/start.sh"
        REPORT_DIR       = "${env.WORKSPACE}/reports"
        NUGET_SOURCE     = 'https://repo.huaweicloud.com/repository/nuget/v3/index.json'
    }

    triggers { pollSCM('H/5 * * * *') }

    stages {
        stage('Preflight') {
            steps {
                sh '''
                    set -e
                    echo "=== Jenkins user ==="
                    id
                    echo ""
                    echo "=== Docker access ==="
                    docker info --format 'Server Version: {{.ServerVersion}}'
                    echo ""
                    echo "=== dotnet SDK ==="
                    dotnet --version
                    echo ""
                    echo "=== Repo symlink ==="
                    ls -la "$REPO_DIR" | head -10
                    echo ""
                    echo "=== QuestionBank source tree ==="
                    ls -la "$SERVICE_DIR"
                    echo ""
                    echo "=== Build script ==="
                    ls -la "$BUILD_SCRIPT"
                '''
            }
        }

        stage('Build Image') {
            steps {
                sh '''
                    set -e
                    cd "$REPO_DIR"
                    bash "$BUILD_SCRIPT"
                    docker images ruoyu-questionbank --format '{{.Repository}}:{{.Tag}} {{.CreatedSince}} {{.Size}}'
                '''
            }
        }

        stage('Deploy') {
            steps {
                sh '''
                    set -e
                    cd "$SERVICE_DIR"
                    bash "$START_SCRIPT" &
                    START_PID=$!
                    sleep 20
                    kill $START_PID 2>/dev/null || true
                    docker ps --filter 'name=ruoyu-questionbank' --format '{{.Names}} {{.Status}}'
                '''
            }
        }

        stage('Smoke Test') {
            steps {
                sh '''
                    set +e
                    for i in $(seq 1 30); do
                        CODE=$(docker run --rm --network ruoyu-net curlimages/curl:latest -s -o /dev/null -w "%{http_code}" --max-time 3 http://ruoyu-questionbank:5007/health 2>/dev/null || echo 000)
                        if [ "$CODE" = "200" ]; then
                            echo "QuestionBank ready after ${i}s"
                            break
                        fi
                        echo "Attempt $i: HTTP $CODE, retrying..."
                        sleep 1
                    done
                    CODE=$(docker run --rm --network ruoyu-net curlimages/curl:latest -s -o /dev/null -w "%{http_code}" --max-time 5 http://ruoyu-questionbank:5007/health 2>/dev/null || echo 000)
                    if [ "$CODE" = "200" ]; then
                        echo "QuestionBank smoke test PASSED (HTTP $CODE)"
                        exit 0
                    else
                        echo "QuestionBank smoke test FAILED (HTTP $CODE)"
                        exit 1
                    fi
                '''
            }
        }
    }

    post {
        always {
            echo "=== Collecting artifacts ==="
            script {
                sh '''
                    mkdir -p "$REPORT_DIR"
                    ls -la "$REPORT_DIR/" || true
                '''
            }
            archiveArtifacts artifacts: 'reports/**', allowEmptyArchive: true
        }
        success {
            echo 'QuestionBank pipeline PASSED: build + deploy + smoke'
        }
        failure {
            echo 'QuestionBank pipeline FAILED — check logs above'
        }
    }
}
