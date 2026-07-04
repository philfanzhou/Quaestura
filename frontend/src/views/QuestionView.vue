<template>
  <div class="question-view">
    <div class="card">
      <div class="card-header">
        <span>题目管理</span>
      </div>
      <div class="card-body">
        <div class="form-row">
          <div class="form-group">
            <label>学科</label>
            <div class="select-wrap input-narrow">
              <select v-model.number="searchForm.subject">
                <option v-for="s in subjectOptions" :key="s.value" :value="s.value">{{ s.label }}</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>年级</label>
            <div class="select-wrap input-narrow">
              <select v-model.number="searchForm.grade">
                <option v-for="g in 12" :key="g" :value="g">{{ g }}年级</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>难度</label>
            <div class="select-wrap input-narrow">
              <select v-model="searchForm.level">
                <option :value="undefined">全部</option>
                <option v-for="l in levelOptions" :key="l.value" :value="l.value">{{ l.label }}</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>题型</label>
            <div class="select-wrap input-narrow">
              <select v-model="searchForm.type">
                <option :value="undefined">全部</option>
                <option v-for="t in typeOptions" :key="t.value" :value="t.value">{{ t.label }}</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>关键字</label>
            <div class="input-wrap input-wide">
              <input v-model="searchForm.keyword" placeholder="题干关键字" />
            </div>
          </div>
          <div class="form-group">
            <label>标签</label>
            <div class="input-wrap input-wide">
              <input v-model="searchForm.tagId" placeholder="Tag UUID" />
            </div>
          </div>
          <div class="form-group">
            <button class="btn btn-primary" @click="onSearch">搜索</button>
            <button class="btn btn-secondary btn-gap" @click="resetSearch">重置</button>
          </div>
        </div>

        <div class="table-scroll">
          <div v-if="loading" class="loading-spinner">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            <span>加载中...</span>
          </div>
          <table v-else class="data-table">
            <thead>
              <tr>
                <th style="width: 200px">ID</th>
                <th style="width: 70px">学科</th>
                <th style="width: 70px">年级</th>
                <th style="width: 70px">难度</th>
                <th style="width: 70px">题型</th>
                <th>题干</th>
                <th style="width: 170px">创建时间</th>
                <th style="width: 150px">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in list" :key="row.id">
                <td><span class="id-cell text-ellipsis" :title="row.id">{{ row.id }}</span></td>
                <td>{{ subjectLabel(row.subject) }}</td>
                <td>{{ row.grade }}</td>
                <td>{{ row.level }}</td>
                <td>{{ row.type }}</td>
                <td class="text-ellipsis" :title="row.content">{{ row.content || '(空)' }}</td>
                <td class="text-muted-sm">{{ row.createdAt }}</td>
                <td>
                  <div class="table-actions">
                    <button class="btn btn-secondary btn-small" @click="viewDetail(row)">查看</button>
                    <button class="btn btn-danger btn-small" @click="askDelete(row)">删除</button>
                  </div>
                </td>
              </tr>
              <tr v-if="!loading && list.length === 0">
                <td colspan="8">
                  <div class="empty-state">
                    <div class="empty-state-text">暂无数据</div>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="pagination-bar">
          <span class="pagination-info">共 {{ total }} 条</span>
          <select v-model.number="searchForm.size" class="page-size-select" @change="onSizeChange">
            <option :value="10">10 条/页</option>
            <option :value="20">20 条/页</option>
            <option :value="50">50 条/页</option>
          </select>
          <button class="page-btn" :disabled="searchForm.page <= 1" @click="goPage(searchForm.page - 1)">‹</button>
          <template v-for="p in displayPages" :key="p">
            <span v-if="p < 0" class="page-ellipsis">…</span>
            <button v-else class="page-btn" :class="{ active: p === searchForm.page }" @click="goPage(p)">{{ p }}</button>
          </template>
          <button class="page-btn" :disabled="searchForm.page >= totalPages" @click="goPage(searchForm.page + 1)">›</button>
        </div>
      </div>
    </div>

    <!-- 详情弹窗 -->
    <div v-if="detailVisible" class="modal-overlay" @click.self="detailVisible = false">
      <div class="modal modal-lg">
        <div class="modal-header">
          <span class="modal-title">题目详情</span>
          <button class="modal-close" @click="detailVisible = false">×</button>
        </div>
        <div class="modal-body">
          <div v-if="detail">
            <div class="detail-field">
              <div class="detail-label">ID</div>
              <div class="detail-value id-cell">{{ detail.id }}</div>
            </div>
            <div class="detail-field">
              <div class="detail-label">学科 / 年级</div>
              <div class="detail-value">{{ subjectLabel(detail.subject) }} / {{ detail.grade }}</div>
            </div>
            <div class="detail-field">
              <div class="detail-label">难度 / 题型</div>
              <div class="detail-value">{{ detail.level }} / {{ detail.type }}</div>
            </div>
            <div class="detail-field">
              <div class="detail-label">题干</div>
              <pre class="content-pre">{{ detail.content || '(空)' }}</pre>
            </div>
            <div class="detail-field">
              <div class="detail-label">正确答案</div>
              <pre class="content-pre">{{ detail.correctAnswer || '(空)' }}</pre>
            </div>
            <div class="detail-field">
              <div class="detail-label">解析</div>
              <pre class="content-pre">{{ detail.analysis || '(空)' }}</pre>
            </div>
            <div v-if="detail.picturePaths.length > 0" class="detail-field">
              <div class="detail-label">图片</div>
              <div class="image-grid">
                <img
                  v-for="(url, idx) in detail.picturePaths"
                  :key="idx"
                  :src="url"
                  alt="题目图片"
                  @click="openImage(url)"
                />
              </div>
            </div>
          </div>
          <div v-else class="loading-spinner">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            <span>加载中...</span>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" @click="detailVisible = false">关闭</button>
        </div>
      </div>
    </div>

    <!-- 删除确认弹窗 -->
    <div v-if="deleteTarget" class="modal-overlay" @click.self="deleteTarget = null">
      <div class="modal">
        <div class="modal-header">
          <span class="modal-title">确认删除</span>
          <button class="modal-close" @click="deleteTarget = null">×</button>
        </div>
        <div class="modal-body">
          <div class="detail-value">确定删除题目 <span class="id-cell">{{ deleteTarget.id }}</span> 吗？此操作不可恢复。</div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" :disabled="deleting" @click="deleteTarget = null">取消</button>
          <button class="btn btn-danger" :disabled="deleting" @click="confirmDelete">
            <svg v-if="deleting" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="spinner">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            确认删除
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { getQuestionAdminApiClient } from '../services/questionApi'
import { getApiErrorMessage } from '../components/ApiErrorHandler'
import { showToast } from '../utils/toast'
import type { Question } from '../types'

const subjectOptions = [
  { value: 1, label: '语文' },
  { value: 2, label: '数学' },
  { value: 3, label: '英语' },
  { value: 4, label: '物理' },
  { value: 5, label: '化学' },
  { value: 6, label: '生物' },
  { value: 7, label: '历史' },
  { value: 8, label: '地理' },
  { value: 9, label: '政治' },
]

const levelOptions = [
  { value: 1, label: '1-简单' },
  { value: 2, label: '2-中等' },
  { value: 3, label: '3-困难' },
]

const typeOptions = [
  { value: 1, label: '1-单选' },
  { value: 2, label: '2-多选' },
  { value: 3, label: '3-判断' },
  { value: 4, label: '4-填空' },
  { value: 5, label: '5-问答' },
]

function subjectLabel(value: number): string {
  return subjectOptions.find((s) => s.value === value)?.label ?? String(value)
}

const list = ref<Question[]>([])
const total = ref(0)
const loading = ref(false)
const detailVisible = ref(false)
const detail = ref<Question | null>(null)
const deleteTarget = ref<Question | null>(null)
const deleting = ref(false)

const searchForm = reactive({
  subject: 1,
  grade: 7,
  level: undefined as number | undefined,
  type: undefined as number | undefined,
  keyword: '',
  tagId: '',
  page: 1,
  size: 10,
})

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / searchForm.size)))

const displayPages = computed<number[]>(() => {
  const pages: number[] = []
  const totalP = totalPages.value
  const current = searchForm.page
  if (totalP <= 7) {
    for (let i = 1; i <= totalP; i++) pages.push(i)
    return pages
  }
  pages.push(1)
  let start = Math.max(2, current - 2)
  let end = Math.min(totalP - 1, current + 2)
  if (start > 2) pages.push(-1)
  for (let i = start; i <= end; i++) pages.push(i)
  if (end < totalP - 1) pages.push(-2)
  pages.push(totalP)
  return pages
})

async function loadList() {
  if (!searchForm.subject || !searchForm.grade) {
    showToast('请填写学科和年级', 'warning')
    return
  }
  loading.value = true
  try {
    const result = await getQuestionAdminApiClient().search({
      subject: searchForm.subject,
      grade: searchForm.grade,
      level: searchForm.level,
      type: searchForm.type,
      keyword: searchForm.keyword || undefined,
      tagId: searchForm.tagId || undefined,
      page: searchForm.page,
      size: searchForm.size,
    })
    list.value = result.data
    total.value = result.total
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    loading.value = false
  }
}

function onSearch() {
  searchForm.page = 1
  loadList()
}

function onSizeChange() {
  searchForm.page = 1
  loadList()
}

function goPage(p: number) {
  if (p < 1 || p > totalPages.value || p === searchForm.page) return
  searchForm.page = p
  loadList()
}

function resetSearch() {
  searchForm.level = undefined
  searchForm.type = undefined
  searchForm.keyword = ''
  searchForm.tagId = ''
  searchForm.page = 1
  loadList()
}

async function viewDetail(row: Question) {
  detail.value = null
  detailVisible.value = true
  try {
    detail.value = await getQuestionAdminApiClient().getById(row.id)
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
    detailVisible.value = false
  }
}

function openImage(url: string) {
  window.open(url, '_blank')
}

function askDelete(row: Question) {
  deleteTarget.value = row
}

async function confirmDelete() {
  if (!deleteTarget.value) return
  deleting.value = true
  try {
    await getQuestionAdminApiClient().delete(deleteTarget.value.id)
    showToast('删除成功', 'success')
    deleteTarget.value = null
    loadList()
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    deleting.value = false
  }
}

onMounted(loadList)
</script>

<style scoped>
.question-view {
  max-width: 1400px;
}
</style>
