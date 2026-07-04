<template>
  <div class="knowledge-view">
    <div class="card">
      <div class="card-header">
        <span>知识点管理</span>
        <button class="btn btn-primary btn-small" @click="openCreate">+ 新增知识点</button>
      </div>
      <div class="card-body">
        <div class="form-row">
          <div class="form-group">
            <label>名称</label>
            <div class="input-wrap input-wide">
              <input v-model="searchForm.name" placeholder="模糊搜索" @keyup.enter="onSearch" />
            </div>
          </div>
          <div class="form-group">
            <label>学科</label>
            <div class="input-wrap input-narrow">
              <input v-model.number="searchForm.subject" type="number" placeholder="可选" />
            </div>
          </div>
          <div class="form-group">
            <label>年级</label>
            <div class="input-wrap input-narrow">
              <input v-model.number="searchForm.grade" type="number" placeholder="可选" />
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
                <th>名称</th>
                <th>描述</th>
                <th style="width: 70px">学科</th>
                <th style="width: 70px">年级</th>
                <th style="width: 80px">引用</th>
                <th style="width: 170px">创建时间</th>
                <th style="width: 200px">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in list" :key="row.id">
                <td>
                  <span :style="{ paddingLeft: depthOf(row) * 20 + 'px' }" class="knowledge-name">
                    <span v-if="depthOf(row) > 0" class="tree-indent">└</span>
                    {{ row.name }}
                  </span>
                </td>
                <td class="text-ellipsis" :title="row.description || ''">{{ row.description || '—' }}</td>
                <td>{{ row.subject || '—' }}</td>
                <td>{{ row.grade || '—' }}</td>
                <td>
                  <span v-if="row.isReferenced" class="tag tag-success">是</span>
                  <span v-else class="tag tag-info">否</span>
                </td>
                <td class="text-muted-sm">{{ row.createdAt }}</td>
                <td>
                  <div class="table-actions">
                    <button class="btn btn-secondary btn-small" @click="openEdit(row)">编辑</button>
                    <button class="btn btn-secondary btn-small" @click="addChild(row)">+ 子级</button>
                    <button class="btn btn-danger btn-small" :disabled="row.isReferenced" @click="askDelete(row)">删除</button>
                  </div>
                </td>
              </tr>
              <tr v-if="!loading && list.length === 0">
                <td colspan="7">
                  <div class="empty-state">
                    <div class="empty-state-text">暂无数据</div>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div v-if="!isNameSearch" class="pagination-bar">
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

    <!-- 新增/编辑弹窗 -->
    <div v-if="dialogVisible" class="modal-overlay" @click.self="closeDialog">
      <div class="modal modal-md">
        <div class="modal-header">
          <span class="modal-title">{{ dialogTitle }}</span>
          <button class="modal-close" @click="closeDialog">×</button>
        </div>
        <div class="modal-body">
          <div class="form-group">
            <label>名称 <span class="required-mark">*</span></label>
            <div class="input-wrap">
              <input v-model="form.name" maxlength="255" placeholder="知识点名称" />
            </div>
          </div>
          <div class="form-group">
            <label>父知识点</label>
            <div class="input-wrap">
              <input v-model="form.parentId" placeholder="父节点 ID（可选）" />
            </div>
            <span class="hint">UUID，留空则为顶级节点</span>
          </div>
          <div class="form-group">
            <label>学科</label>
            <div class="input-wrap input-medium">
              <input v-model.number="form.subject" type="number" placeholder="可选" />
            </div>
          </div>
          <div class="form-group">
            <label>年级</label>
            <div class="input-wrap input-medium">
              <input v-model.number="form.grade" type="number" placeholder="可选" />
            </div>
          </div>
          <div class="form-group">
            <label>描述</label>
            <textarea v-model="form.description" class="input" rows="3" maxlength="1000"></textarea>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" :disabled="submitting" @click="closeDialog">取消</button>
          <button class="btn btn-primary" :disabled="submitting" @click="submit">
            <svg v-if="submitting" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="spinner">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            保存
          </button>
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
          <div class="detail-value">确定删除知识点「{{ deleteTarget.name }}」吗？</div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" :disabled="deleting" @click="deleteTarget = null">取消</button>
          <button class="btn btn-danger" :disabled="deleting" @click="confirmDelete">确认删除</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { getKnowledgeAdminApiClient } from '../services/knowledgeApi'
import { getApiErrorMessage } from '../components/ApiErrorHandler'
import { showToast } from '../utils/toast'
import type { Knowledge, CreateKnowledgeRequest } from '../types'

const USER_ID = '00000000-0000-0000-0000-000000000001'

const list = ref<Knowledge[]>([])
const total = ref(0)
const loading = ref(false)
const dialogVisible = ref(false)
const submitting = ref(false)
const editingId = ref<string | null>(null)
const parentOfChild = ref<string | null>(null)
const deleteTarget = ref<Knowledge | null>(null)
const deleting = ref(false)

const searchForm = reactive({
  name: '',
  subject: undefined as number | undefined,
  grade: undefined as number | undefined,
  page: 1,
  size: 10,
})

const form = reactive<CreateKnowledgeRequest>({
  id: undefined,
  parentId: undefined,
  name: '',
  description: undefined,
  subject: undefined,
  grade: undefined,
  userId: USER_ID,
})

const isNameSearch = computed(() => !!searchForm.name.trim())
const dialogTitle = computed(() => {
  if (editingId.value) return '编辑知识点'
  if (parentOfChild.value) return '新增子知识点'
  return '新增知识点'
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
  const start = Math.max(2, current - 2)
  const end = Math.min(totalP - 1, current + 2)
  if (start > 2) pages.push(-1)
  for (let i = start; i <= end; i++) pages.push(i)
  if (end < totalP - 1) pages.push(-2)
  pages.push(totalP)
  return pages
})

const nodeMap = computed<Map<string, Knowledge>>(() => {
  const m = new Map<string, Knowledge>()
  for (const item of list.value) m.set(item.id, item)
  return m
})

function depthOf(row: Knowledge): number {
  let depth = 0
  let current = row
  const guard = new Set<string>()
  while (current.parentId && nodeMap.value.has(current.parentId) && !guard.has(current.id)) {
    guard.add(current.id)
    const parent = nodeMap.value.get(current.parentId)
    if (!parent) break
    current = parent
    depth++
    if (depth > 50) break
  }
  return depth
}

async function loadList() {
  loading.value = true
  try {
    const result = await getKnowledgeAdminApiClient().list({
      name: searchForm.name || undefined,
      subject: searchForm.subject,
      grade: searchForm.grade,
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
  searchForm.name = ''
  searchForm.subject = undefined
  searchForm.grade = undefined
  searchForm.page = 1
  loadList()
}

function openCreate() {
  editingId.value = null
  parentOfChild.value = null
  Object.assign(form, { id: undefined, parentId: undefined, name: '', description: undefined, subject: undefined, grade: undefined, userId: USER_ID })
  dialogVisible.value = true
}

function openEdit(row: Knowledge) {
  editingId.value = row.id
  parentOfChild.value = null
  Object.assign(form, {
    id: row.id,
    parentId: row.parentId || undefined,
    name: row.name,
    description: row.description || undefined,
    subject: row.subject || undefined,
    grade: row.grade || undefined,
    userId: USER_ID,
  })
  dialogVisible.value = true
}

function addChild(row: Knowledge) {
  editingId.value = null
  parentOfChild.value = row.id
  Object.assign(form, {
    id: undefined,
    parentId: row.id,
    name: '',
    description: undefined,
    subject: row.subject || undefined,
    grade: row.grade || undefined,
    userId: USER_ID,
  })
  dialogVisible.value = true
}

function closeDialog() {
  if (submitting.value) return
  dialogVisible.value = false
}

async function submit() {
  if (!form.name.trim()) {
    showToast('请填写名称', 'warning')
    return
  }
  submitting.value = true
  try {
    await getKnowledgeAdminApiClient().upsert({ ...form, name: form.name.trim() })
    showToast(editingId.value ? '更新成功' : '创建成功', 'success')
    dialogVisible.value = false
    loadList()
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    submitting.value = false
  }
}

function askDelete(row: Knowledge) {
  deleteTarget.value = row
}

async function confirmDelete() {
  if (!deleteTarget.value) return
  deleting.value = true
  try {
    await getKnowledgeAdminApiClient().delete(deleteTarget.value.id)
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
.knowledge-view {
  max-width: 1400px;
}
.knowledge-name {
  display: inline-block;
}
.tree-indent {
  color: var(--text-muted);
  margin-right: 4px;
}
.required-mark {
  color: var(--danger-color);
}
</style>
